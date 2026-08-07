using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Dyract.Core.Identity;
using Dyract.Crypto.Signatures;
using Dyract.Protocol;
using Dyract.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IdentityStore>();
builder.Services.AddSingleton<RegistrationChallengeStore>();
builder.Services.AddSingleton<ReplayNonceStore>();
builder.Services.AddSingleton<PresenceStore>();
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new
{
    service = "dyract-directory",
    status = "ok",
    protocol = 1
}));

var api = app.MapGroup("/api/v1");
api.MapPost("/identity/challenge", CreateRegistrationChallenge);
api.MapPost("/identity/register", RegisterPeer);
api.MapPost("/peer/lookup", LookupPeer);
api.MapPost("/presence", PublishPresence);
api.MapPost("/presence/remove", RemovePresence);
api.MapPost("/peer/resolve", ResolvePeer);

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

static IResult RegisterPeer(
    RegisterPeerRequest request,
    RegistrationChallengeStore challenges,
    IdentityStore identities,
    TimeProvider timeProvider)
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

    if (!identities.TryRegister(peerId, publicKey, now, out var registeredPeer))
    {
        return Results.Conflict(new ApiError("identity_conflict", "Peer ID is already bound to different key material."));
    }

    return Results.Ok(new RegisterPeerResponse(
        registeredPeer.PeerId.Value,
        registeredPeer.RegisteredAt.ToUnixTimeSeconds()));
}

static IResult LookupPeer(
    PeerLookupRequest request,
    IdentityStore identities,
    ReplayNonceStore replayNonces,
    TimeProvider timeProvider)
{
    if (!PeerId.TryParse(request.RequesterPeerId, out var requesterId) ||
        !PeerId.TryParse(request.TargetPeerId, out var targetId))
    {
        return BadRequest("invalid_peer_id", "RequesterPeerId or TargetPeerId is invalid.");
    }

    if (!identities.TryGet(requesterId, out var requester))
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

    if (!replayNonces.TryAccept(requesterId, request.Nonce, now))
    {
        return Unauthorized("replay_detected", "This signed lookup nonce has already been used.");
    }

    if (!identities.TryGet(targetId, out var target))
    {
        return Results.NotFound(new ApiError("peer_not_found", "Target peer is not registered."));
    }

    return Results.Ok(new PeerLookupResponse(
        target.PeerId.Value,
        Convert.ToBase64String(target.PublicKey),
        target.RegisteredAt.ToUnixTimeSeconds()));
}

static IResult PublishPresence(
    PublishPresenceRequest request,
    IdentityStore identities,
    PresenceStore presence,
    ReplayNonceStore replayNonces,
    TimeProvider timeProvider)
{
    if (!PeerId.TryParse(request.PeerId, out var peerId))
    {
        return BadRequest("invalid_peer_id", "PeerId is invalid.");
    }

    if (!identities.TryGet(peerId, out var identity))
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

    if (!replayNonces.TryAccept(peerId, request.Nonce, now))
    {
        return Unauthorized("replay_detected", "This signed presence nonce has already been used.");
    }

    var lease = presence.Publish(peerId, request.Candidates, now, leaseExpires);

    return Results.Ok(new PublishPresenceResponse(
        lease.PeerId.Value,
        lease.ExpiresAt.ToUnixTimeSeconds()));
}

static IResult RemovePresence(
    RemovePresenceRequest request,
    IdentityStore identities,
    PresenceStore presence,
    ReplayNonceStore replayNonces,
    TimeProvider timeProvider)
{
    if (!PeerId.TryParse(request.PeerId, out var peerId))
    {
        return BadRequest("invalid_peer_id", "PeerId is invalid.");
    }

    if (!identities.TryGet(peerId, out var identity))
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

    if (!replayNonces.TryAccept(peerId, request.Nonce, now))
    {
        return Unauthorized("replay_detected", "This signed presence nonce has already been used.");
    }

    presence.Remove(peerId);
    return Results.NoContent();
}

static IResult ResolvePeer(
    ResolvePeerRequest request,
    IdentityStore identities,
    PresenceStore presence,
    ReplayNonceStore replayNonces,
    TimeProvider timeProvider)
{
    if (!PeerId.TryParse(request.RequesterPeerId, out var requesterId) ||
        !PeerId.TryParse(request.TargetPeerId, out var targetId))
    {
        return BadRequest("invalid_peer_id", "RequesterPeerId or TargetPeerId is invalid.");
    }

    if (!identities.TryGet(requesterId, out var requester))
    {
        return Unauthorized("requester_unknown", "Requester is not a registered Dyract peer.");
    }

    if (!identities.TryGet(targetId, out var target))
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

    var capabilityError = ValidateContactCapability(
        request.Capability,
        requesterId,
        targetId,
        target.PublicKey,
        now);

    if (capabilityError is not null)
    {
        return capabilityError;
    }

    if (!replayNonces.TryAccept(requesterId, request.Nonce, now))
    {
        return Unauthorized("replay_detected", "This signed resolve nonce has already been used.");
    }

    if (!presence.TryGet(targetId, now, out var lease))
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

static IResult? ValidateContactCapability(
    ContactCapability capability,
    PeerId requesterId,
    PeerId targetId,
    byte[] targetPublicKey,
    DateTimeOffset now)
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

    if (!TryUnixTime(capability.IssuedUnixSeconds, out var issuedAt) ||
        !TryUnixTime(capability.ExpiresUnixSeconds, out var expiresAt) ||
        expiresAt <= issuedAt ||
        issuedAt > now.AddMinutes(2) ||
        expiresAt <= now)
    {
        return Unauthorized("capability_expired", "Contact capability is invalid or expired.");
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
    => value is { Length: 32 } && value.All(Uri.IsHexDigit);

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
