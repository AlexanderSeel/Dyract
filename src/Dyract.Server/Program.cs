using System.Security.Cryptography;
using Dyract.Core.Identity;
using Dyract.Crypto.Signatures;
using Dyract.Protocol;
using Dyract.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IdentityStore>();
builder.Services.AddSingleton<RegistrationChallengeStore>();
builder.Services.AddSingleton<ReplayNonceStore>();
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
        return Results.Json(
            new ApiError("requester_unknown", "Requester is not a registered Dyract peer."),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var now = timeProvider.GetUtcNow();
    DateTimeOffset requestTime;

    try
    {
        requestTime = DateTimeOffset.FromUnixTimeSeconds(request.TimestampUnixSeconds);
    }
    catch (ArgumentOutOfRangeException)
    {
        return BadRequest("invalid_timestamp", "Timestamp is outside the valid Unix time range.");
    }

    if (Math.Abs((now - requestTime).TotalSeconds) > 120)
    {
        return Results.Json(
            new ApiError("request_expired", "Signed lookup timestamp is outside the accepted time window."),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    if (!TryDecodeBase64(request.Nonce, 128, out var nonceBytes) || nonceBytes.Length < 16)
    {
        return BadRequest("invalid_nonce", "Nonce must contain at least 16 random bytes encoded as base64.");
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
        return Results.Json(
            new ApiError("signature_invalid", "Lookup signature could not be verified."),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    if (!replayNonces.TryAccept(requesterId, request.Nonce, now))
    {
        return Results.Json(
            new ApiError("replay_detected", "This signed lookup nonce has already been used."),
            statusCode: StatusCodes.Status401Unauthorized);
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

static IResult BadRequest(string code, string message)
    => Results.BadRequest(new ApiError(code, message));

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
