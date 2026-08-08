using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Dyract.Core.Identity;
using Dyract.Crypto.Signatures;
using Dyract.Protocol;
using Dyract.Server.Services;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using StackExchange.Redis;

const long MaxRequestBodyBytes = 64 * 1024;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.MaxDepth = 16;
});

var identityConnectionString = builder.Configuration.GetConnectionString("Dyract");
if (string.IsNullOrWhiteSpace(identityConnectionString))
{
    builder.Services.AddSingleton<IIdentityStore, InMemoryIdentityStore>();
    builder.Services.AddSingleton<ICapabilityRevocationStore, CapabilityRevocationStore>();
}
else
{
    var postgresConnectionString = identityConnectionString;
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgresConnectionString));
    builder.Services.AddSingleton<IIdentityStore, PostgresIdentityStore>();
    builder.Services.AddSingleton<ICapabilityRevocationStore, PostgresCapabilityRevocationStore>();
    builder.Services.AddHostedService<PostgresSchemaInitializer>();
}

var redisConnectionString = builder.Configuration.GetConnectionString("Redis");
if (string.IsNullOrWhiteSpace(redisConnectionString))
{
    builder.Services.AddSingleton<IPresenceStore, PresenceStore>();
    builder.Services.AddSingleton<IReplayNonceStore, ReplayNonceStore>();
}
else
{
    var redisOptions = ConfigurationOptions.Parse(redisConnectionString);
    redisOptions.AbortOnConnectFail = true;
    redisOptions.ClientName = "dyract-directory";
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));
    builder.Services.AddSingleton<IPresenceStore, RedisPresenceStore>();
    builder.Services.AddSingleton<IReplayNonceStore, RedisReplayNonceStore>();
    builder.Services.AddHostedService<RedisTransientStateInitializer>();
}

builder.Services.AddSingleton<RegistrationChallengeStore>();
builder.Services.AddSingleton<SignalStore>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("registration", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: GetClientPartitionKey(context),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            }));

    options.AddPolicy("peer-operations", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: GetClientPartitionKey(context),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 240,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            }));

    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new ApiError("rate_limited", "Too many directory requests. Retry after the current rate-limit window."),
            cancellationToken: cancellationToken);
    };
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (context.Request.ContentLength is > MaxRequestBodyBytes)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        await context.Response.WriteAsJsonAsync(
            new ApiError("request_too_large", $"Request body must not exceed {MaxRequestBodyBytes} bytes."),
            cancellationToken: context.RequestAborted);
        return;
    }

    await next();
});

app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new
{
    service = "dyract-directory",
    status = "ok",
    protocol = 1
}));

var api = app.MapGroup("/api/v1");
api.MapPost("/identity/challenge", CreateRegistrationChallenge)
    .RequireRateLimiting("registration");
api.MapPost("/identity/register", RegisterPeer)
    .RequireRateLimiting("registration");
api.MapPost("/peer/lookup", LookupPeer)
    .RequireRateLimiting("peer-operations");
api.MapPost("/presence", PublishPresence)
    .RequireRateLimiting("peer-operations");
api.MapPost("/presence/remove", RemovePresence)
    .RequireRateLimiting("peer-operations");
api.MapPost("/capability/revoke", RevokeCapability)
    .RequireRateLimiting("peer-operations");
api.MapPost("/peer/resolve", ResolvePeer)
    .RequireRateLimiting("peer-operations");
api.MapPeerSignaling();

app.Run();

static IResult CreateRegistrationChallenge(
    RegistrationChallengeRequest request,
    RegistrationChallengeStore challenges,
    TimeProvider timeProvider)
{
    if (!TryDecodeBase64(request.PublicKey, 4096, out var publicKey) ||
        !SignatureVerifier.IsValidIdentityPublicKey(publicKey))
    {
        return BadRequest("invalid_public_key", "PublicKey must be a valid Dyract P-256 SubjectPublicKeyInfo value.");
    }

    var peerId = PeerId.FromPublicKey(publicKey);
    var challenge = challenges.Create(peerId, publicKey, timeProvider.GetUtcNow());

    return Results.Ok(new RegistrationChallengeResponse(
        peerId.Value,
        challenge.Id,
        Convert.ToBase64String(challenge.ChallengeBytes),
        challenge.ExpiresAt.ToUnixTimeSeconds()));
}

static async Task<IResult> RegisterPeer(
    RegisterPeerRequest request,
    RegistrationChallengeStore challenges,
    IIdentityStore identities,
    TimeProvider timeProvider,
    CancellationToken cancellationToken)
{
    if (!PeerId.TryParse(request.PeerId, out var peerId))
    {
        return BadRequest("invalid_peer_id", "PeerId is invalid.");
    }

    if (!TryDecodeBase64(request.PublicKey, 4096, out var publicKey) ||
        !SignatureVerifier.IsValidIdentityPublicKey(publicKey))
    {
        return BadRequest("invalid_public_key", "PublicKey is invalid.");
    }

    if (PeerId.FromPublicKey(publicKey) != peerId)
    {
        return BadRequest("peer_id_mismatch", "PeerId does not match the supplied public key.");
    }

    var now = timeProvider.GetUtcNow();

    if (!challenges.TryGet(request.ChallengeId, now, out var challenge))
    {
        return Results.Json(
            new ApiError("challenge_invalid", "Registration challenge is unknown or expired."),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    if (challenge.PeerId != peerId ||
        !CryptographicOperations.FixedTimeEquals(challenge.PublicKey, publicKey))
    {
        return Results.Json(
            new ApiError("challenge_mismatch", "Registration challenge does not belong to this identity."),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    if (!TryDecodeBase64(request.Signature, 512, out var signature))
    {
        return BadRequest("invalid_signature", "Signature must be valid base64.");
    }

    var publicKeyBase64 = Convert.ToBase64String(publicKey);
    var challengeBase64 = Convert.ToBase64String(challenge.ChallengeBytes);
    var proof = ProofPayload.ForRegistration(
        challenge.Id,
        peerId.Value,
        publicKeyBase64,
        challengeBase64);

    if (!SignatureVerifier.Verify(publicKey, proof, signature))
    {
        return Results.Json(
            new ApiError("signature_invalid", "Registration signature could not be verified."),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    if (!challenges.TryConsume(challenge.Id))
    {
        return Results.Conflict(new ApiError("challenge_consumed", "Registration challenge has already been consumed."));
    }

    var registration = await identities.RegisterAsync(
        peerId,
        publicKey,
        now,
        cancellationToken);

    if (!registration.IsAccepted)
    {
        return Results.Conflict(new ApiError("identity_conflict", "Peer ID is already bound to different key material."));
    }

    return Results.Ok(new RegisterPeerResponse(
        registration.Peer.PeerId.Value,
        registration.Peer.RegisteredAt.ToUnixTimeSeconds()));
}

static async Task<IResult> LookupPeer(
    PeerLookupRequest request,
    IIdentityStore identities,
    IReplayNonceStore replayNonces,
    TimeProvider timeProvider,
    CancellationToken cancellationToken)
{
    if (!PeerId.TryParse(request.RequesterPeerId, out var requesterId) ||
        !PeerId.TryParse(request.TargetPeerId, out var targetId))
    {
        return BadRequest("invalid_peer_id", "RequesterPeerId or TargetPeerId is invalid.");
    }

    var requester = await identities.GetAsync(requesterId, cancellationToken);
    if (requester is null)
    {
        return Unauthorized("requester_unknown", "Requester is not a registered Dyract peer.");
    }

    var now = timeProvider.GetUtcNow();
    if (!TryValidateSignedRequestMetadata(request.TimestampUnixSeconds, request.Nonce, now, out var metadataError))
    {
        return metadataError!;
    }

    if (!TryDecodeBase64(request.Signature, 512, out var signature))
    {
        return BadRequest("invalid_signature", "Signature must be valid base64.");
    }

    var proof = ProofPayload.ForLookup(
        requesterId.Value,
        targetId.Value,
        request.TimestampUnixSeconds,
        request.Nonce);

    if (!SignatureVerifier.Verify(requester.PublicKey, proof, signature))
    {
        return Unauthorized("signature_invalid", "Lookup signature could not be verified.");
    }

    if (!await replayNonces.TryAcceptAsync(requesterId, request.Nonce, now, cancellationToken))
    {
        return Unauthorized("replay_detected", "This signed lookup nonce has already been used.");
    }

    var target = await identities.GetAsync(targetId, cancellationToken);
    if (target is null)
    {
        return Results.NotFound(new ApiError("peer_not_found", "Target peer is not registered."));
    }

    return Results.Ok(new PeerLookupResponse(
        target.PeerId.Value,
        Convert.ToBase64String(target.PublicKey),
        target.RegisteredAt.ToUnixTimeSeconds()));
}

static async Task<IResult> PublishPresence(
    PublishPresenceRequest request,
    IIdentityStore identities,
    IPresenceStore presence,
    IReplayNonceStore replayNonces,
    TimeProvider timeProvider,
    CancellationToken cancellationToken)
{
    if (!PeerId.TryParse(request.PeerId, out var peerId))
    {
        return BadRequest("invalid_peer_id", "PeerId is invalid.");
    }

    var identity = await identities.GetAsync(peerId, cancellationToken);
    if (identity is null)
    {
        return Unauthorized("peer_unknown", "Peer is not registered.");
    }

    if (request.Candidates is null || request.Candidates.Length is < 1 or > 8)
    {
        return BadRequest("invalid_candidates", "Presence must contain between one and eight connection candidates.");
    }

    if (!TryValidateCandidates(request.Candidates, out var candidateError))
    {
        return BadRequest("invalid_candidate", candidateError!);
    }

    var now = timeProvider.GetUtcNow();
    if (!TryValidateSignedRequestMetadata(request.TimestampUnixSeconds, request.Nonce, now, out var metadataError))
    {
        return metadataError!;
    }

    if (!TryUnixTime(request.LeaseExpiresUnixSeconds, out var leaseExpires))
    {
        return BadRequest("invalid_lease", "Presence lease expiry is outside the valid Unix time range.");
    }

    if (leaseExpires <= now || leaseExpires > now.AddMinutes(2))
    {
        return BadRequest("invalid_lease", "Presence lease must expire within the next two minutes.");
    }

    if (!TryDecodeBase64(request.Signature, 512, out var signature))
    {
        return BadRequest("invalid_signature", "Signature must be valid base64.");
    }

    var proof = ProofPayload.ForPresence(
        peerId.Value,
        request.Candidates,
        request.LeaseExpiresUnixSeconds,
        request.TimestampUnixSeconds,
        request.Nonce);

    if (!SignatureVerifier.Verify(identity.PublicKey, proof, signature))
    {
        return Unauthorized("signature_invalid", "Presence signature could not be verified.");
    }

    if (!await replayNonces.TryAcceptAsync(peerId, request.Nonce, now, cancellationToken))
    {
        return Unauthorized("replay_detected", "This signed presence nonce has already been used.");
    }

    var lease = await presence.PublishAsync(peerId, request.Candidates, now, leaseExpires, cancellationToken);

    return Results.Ok(new PublishPresenceResponse(
        lease.PeerId.Value,
        lease.ExpiresAt.ToUnixTimeSeconds()));
}

static async Task<IResult> RemovePresence(
    RemovePresenceRequest request,
    IIdentityStore identities,
    IPresenceStore presence,
    IReplayNonceStore replayNonces,
    TimeProvider timeProvider,
    CancellationToken cancellationToken)
{
    if (!PeerId.TryParse(request.PeerId, out var peerId))
    {
        return BadRequest("invalid_peer_id", "PeerId is invalid.");
    }

    var identity = await identities.GetAsync(peerId, cancellationToken);
    if (identity is null)
    {
        return Unauthorized("peer_unknown", "Peer is not registered.");
    }

    var now = timeProvider.GetUtcNow();
    if (!TryValidateSignedRequestMetadata(request.TimestampUnixSeconds, request.Nonce, now, out var metadataError))
    {
        return metadataError!;
    }

    if (!TryDecodeBase64(request.Signature, 512, out var signature))
    {
        return BadRequest("invalid_signature", "Signature must be valid base64.");
    }

    var proof = ProofPayload.ForPresenceRemoval(
        peerId.Value,
        request.TimestampUnixSeconds,
        request.Nonce);

    if (!SignatureVerifier.Verify(identity.PublicKey, proof, signature))
    {
        return Unauthorized("signature_invalid", "Presence removal signature could not be verified.");
    }

    if (!await replayNonces.TryAcceptAsync(peerId, request.Nonce, now, cancellationToken))
    {
        return Unauthorized("replay_detected", "This signed presence nonce has already been used.");
    }

    await presence.RemoveAsync(peerId, cancellationToken);
    return Results.NoContent();
}

static async Task<IResult> RevokeCapability(
    RevokeContactCapabilityRequest request,
    IIdentityStore identities,
    IReplayNonceStore replayNonces,
    ICapabilityRevocationStore revocations,
    TimeProvider timeProvider,
    CancellationToken cancellationToken)
{
    if (!PeerId.TryParse(request.IssuerPeerId, out var issuerId))
    {
        return BadRequest("invalid_peer_id", "IssuerPeerId is invalid.");
    }

    if (!IsValidCapabilityId(request.CapabilityId))
    {
        return BadRequest("capability_invalid", "CapabilityId must be a 128-bit hexadecimal identifier.");
    }

    var issuer = await identities.GetAsync(issuerId, cancellationToken);
    if (issuer is null)
    {
        return Unauthorized("issuer_unknown", "Capability issuer is not a registered Dyract peer.");
    }

    var now = timeProvider.GetUtcNow();
    if (!TryValidateSignedRequestMetadata(request.TimestampUnixSeconds, request.Nonce, now, out var metadataError))
    {
        return metadataError!;
    }

    if (!TryUnixTime(request.CapabilityExpiresUnixSeconds, out var expiresAt) ||
        expiresAt <= now ||
        expiresAt > now.Add(ContactCapabilityPolicy.MaximumLifetime))
    {
        return BadRequest("capability_expiry", "Revoked capability expiry must be in the future and within the supported capability lifetime.");
    }

    if (!TryDecodeBase64(request.Signature, 512, out var signature))
    {
        return BadRequest("invalid_signature", "Signature must be valid base64.");
    }

    var proof = ProofPayload.ForContactCapabilityRevocation(
        issuerId.Value,
        request.CapabilityId,
        request.CapabilityExpiresUnixSeconds,
        request.TimestampUnixSeconds,
        request.Nonce);

    if (!SignatureVerifier.Verify(issuer.PublicKey, proof, signature))
    {
        return Unauthorized("signature_invalid", "Capability revocation signature could not be verified.");
    }

    if (!await replayNonces.TryAcceptAsync(issuerId, request.Nonce, now, cancellationToken))
    {
        return Unauthorized("replay_detected", "This signed capability revocation nonce has already been used.");
    }

    var result = await revocations.RevokeAsync(
        issuerId,
        request.CapabilityId,
        expiresAt,
        now,
        cancellationToken);
    if (result == CapabilityRevocationResult.CapacityExceeded)
    {
        return Results.Json(
            new ApiError("revocation_limit", "Too many active capability revocations for this issuer."),
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    if (result == CapabilityRevocationResult.Expired)
    {
        return BadRequest("capability_expired", "Capability has already expired.");
    }

    return Results.NoContent();
}

static async Task<IResult> ResolvePeer(
    ResolvePeerRequest request,
    IIdentityStore identities,
    IPresenceStore presence,
    IReplayNonceStore replayNonces,
    ICapabilityRevocationStore revocations,
    TimeProvider timeProvider,
    CancellationToken cancellationToken)
{
    if (!PeerId.TryParse(request.RequesterPeerId, out var requesterId) ||
        !PeerId.TryParse(request.TargetPeerId, out var targetId))
    {
        return BadRequest("invalid_peer_id", "RequesterPeerId or TargetPeerId is invalid.");
    }

    var requester = await identities.GetAsync(requesterId, cancellationToken);
    if (requester is null)
    {
        return Unauthorized("requester_unknown", "Requester is not a registered Dyract peer.");
    }

    var target = await identities.GetAsync(targetId, cancellationToken);
    if (target is null)
    {
        return Results.NotFound(new ApiError("peer_not_found", "Target peer is not registered."));
    }

    if (request.Capability is null)
    {
        return Unauthorized("capability_missing", "A target-issued contact capability is required.");
    }

    var now = timeProvider.GetUtcNow();
    if (!TryValidateSignedRequestMetadata(request.TimestampUnixSeconds, request.Nonce, now, out var metadataError))
    {
        return metadataError!;
    }

    if (!TryDecodeBase64(request.Signature, 512, out var requesterSignature))
    {
        return BadRequest("invalid_signature", "Signature must be valid base64.");
    }

    var resolveProof = ProofPayload.ForResolve(
        requesterId.Value,
        targetId.Value,
        request.Capability.CapabilityId,
        request.TimestampUnixSeconds,
        request.Nonce);

    if (!SignatureVerifier.Verify(requester.PublicKey, resolveProof, requesterSignature))
    {
        return Unauthorized("signature_invalid", "Resolve signature could not be verified.");
    }

    var capabilityError = await ValidateContactCapabilityAsync(
        request.Capability,
        requesterId,
        targetId,
        target.PublicKey,
        revocations,
        now,
        cancellationToken);

    if (capabilityError is not null)
    {
        return capabilityError;
    }

    if (!await replayNonces.TryAcceptAsync(requesterId, request.Nonce, now, cancellationToken))
    {
        return Unauthorized("replay_detected", "This signed resolve nonce has already been used.");
    }

    var lease = await presence.GetAsync(targetId, now, cancellationToken);
    if (lease is null)
    {
        return Results.Ok(new ResolvePeerResponse(
            target.PeerId.Value,
            Convert.ToBase64String(target.PublicKey),
            IsReachable: false,
            Candidates: Array.Empty<ConnectionCandidate>(),
            LeaseExpiresUnixSeconds: null));
    }

    return Results.Ok(new ResolvePeerResponse(
        target.PeerId.Value,
        Convert.ToBase64String(target.PublicKey),
        IsReachable: true,
        Candidates: lease.Candidates,
        LeaseExpiresUnixSeconds: lease.ExpiresAt.ToUnixTimeSeconds()));
}

static async Task<IResult?> ValidateContactCapabilityAsync(
    ContactCapability capability,
    PeerId requesterId,
    PeerId targetId,
    byte[] targetPublicKey,
    ICapabilityRevocationStore revocations,
    DateTimeOffset now,
    CancellationToken cancellationToken)
{
    if (capability.Version != 1)
    {
        return Unauthorized("capability_version", "Contact capability version is not supported.");
    }

    if (!string.Equals(capability.IssuerPeerId, targetId.Value, StringComparison.Ordinal) ||
        !string.Equals(capability.GranteePeerId, requesterId.Value, StringComparison.Ordinal))
    {
        return Unauthorized("capability_scope", "Contact capability is not valid for this requester and target.");
    }

    if (!IsValidCapabilityId(capability.CapabilityId))
    {
        return Unauthorized("capability_invalid", "Contact capability ID is invalid.");
    }

    if (!ContactCapabilityPolicy.IsLifetimeAllowed(
            capability.IssuedUnixSeconds,
            capability.ExpiresUnixSeconds) ||
        !TryUnixTime(capability.IssuedUnixSeconds, out var issuedAt) ||
        !TryUnixTime(capability.ExpiresUnixSeconds, out var expiresAt) ||
        issuedAt > now.AddMinutes(2) ||
        expiresAt <= now)
    {
        return Unauthorized("capability_expired", "Contact capability is invalid or expired.");
    }

    if (await revocations.IsRevokedAsync(targetId, capability.CapabilityId, now, cancellationToken))
    {
        return Unauthorized("capability_revoked", "Contact capability has been revoked by its issuer.");
    }

    if (!TryDecodeBase64(capability.Signature, 512, out var capabilitySignature))
    {
        return Unauthorized("capability_signature", "Contact capability signature is invalid.");
    }

    var capabilityProof = ProofPayload.ForContactCapability(
        capability.IssuerPeerId,
        capability.GranteePeerId,
        capability.CapabilityId,
        capability.IssuedUnixSeconds,
        capability.ExpiresUnixSeconds);

    if (!SignatureVerifier.Verify(targetPublicKey, capabilityProof, capabilitySignature))
    {
        return Unauthorized("capability_signature", "Contact capability signature could not be verified.");
    }

    return null;
}

static bool TryValidateSignedRequestMetadata(
    long timestampUnixSeconds,
    string nonce,
    DateTimeOffset now,
    out IResult? error)
{
    error = null;

    if (!TryUnixTime(timestampUnixSeconds, out var requestTime))
    {
        error = BadRequest("invalid_timestamp", "Timestamp is outside the valid Unix time range.");
        return false;
    }

    if (Math.Abs((now - requestTime).TotalSeconds) > 120)
    {
        error = Unauthorized("request_expired", "Signed request timestamp is outside the accepted time window.");
        return false;
    }

    if (!TryDecodeBase64(nonce, 128, out var nonceBytes) || nonceBytes.Length < 16)
    {
        error = BadRequest("invalid_nonce", "Nonce must contain at least 16 random bytes encoded as base64.");
        return false;
    }

    return true;
}

static bool TryValidateCandidates(
    IReadOnlyList<ConnectionCandidate> candidates,
    out string? error)
{
    error = null;
    var unique = new HashSet<string>(StringComparer.Ordinal);

    foreach (var candidate in candidates)
    {
        if (candidate is null)
        {
            error = "Connection candidates must not contain null values.";
            return false;
        }

        if (candidate.Kind is not ("host" or "srflx" or "relay"))
        {
            error = "Candidate kind must be host, srflx, or relay.";
            return false;
        }

        if (candidate.Protocol is not ("udp" or "tcp"))
        {
            error = "Candidate protocol must be udp or tcp.";
            return false;
        }

        if (candidate.Port is < 1 or > 65535 || candidate.Priority < 0)
        {
            error = "Candidate port or priority is invalid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(candidate.Address) ||
            candidate.Address.Length > 64 ||
            !IPAddress.TryParse(candidate.Address, out var address) ||
            IsUnsafeAddress(address))
        {
            error = "Candidate address must be a usable IPv4 or IPv6 address.";
            return false;
        }

        var key = $"{candidate.Kind}|{candidate.Protocol}|{address}|{candidate.Port}";
        if (!unique.Add(key))
        {
            error = "Duplicate connection candidates are not allowed.";
            return false;
        }
    }

    return true;
}

static bool IsUnsafeAddress(IPAddress address)
{
    if (IPAddress.IsLoopback(address) ||
        address.Equals(IPAddress.Any) ||
        address.Equals(IPAddress.IPv6Any) ||
        address.Equals(IPAddress.Broadcast))
    {
        return true;
    }

    if (address.AddressFamily == AddressFamily.InterNetworkV6)
    {
        return address.IsIPv6Multicast;
    }

    var bytes = address.GetAddressBytes();
    return bytes.Length == 4 && bytes[0] is >= 224 and <= 239;
}

static bool IsValidCapabilityId(string? value)
    => value is { Length: ContactCapabilityPolicy.CapabilityIdHexLength } && value.All(Uri.IsHexDigit);

static bool TryUnixTime(long unixSeconds, out DateTimeOffset value)
{
    try
    {
        value = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        return true;
    }
    catch (ArgumentOutOfRangeException)
    {
        value = default;
        return false;
    }
}

static string GetClientPartitionKey(HttpContext context)
    => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

static IResult BadRequest(string code, string message)
    => Results.BadRequest(new ApiError(code, message));

static IResult Unauthorized(string code, string message)
    => Results.Json(
        new ApiError(code, message),
        statusCode: StatusCodes.Status401Unauthorized);

static bool TryDecodeBase64(string? value, int maximumBytes, out byte[] bytes)
{
    bytes = Array.Empty<byte>();

    if (string.IsNullOrWhiteSpace(value) || value.Length > maximumBytes * 2)
    {
        return false;
    }

    try
    {
        bytes = Convert.FromBase64String(value);
        return bytes.Length <= maximumBytes;
    }
    catch (FormatException)
    {
        return false;
    }
}

public partial class Program
{
}
