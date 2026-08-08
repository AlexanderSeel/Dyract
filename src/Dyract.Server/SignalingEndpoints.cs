using System.Text;
using Dyract.Core.Identity;
using Dyract.Crypto.Signatures;
using Dyract.Protocol;
using Dyract.Server.Services;
using Microsoft.AspNetCore.RateLimiting;

namespace Dyract.Server;

public static class SignalingEndpoints
{
    private const int MaximumPayloadBytes = 32 * 1024;
    private const int MaximumSignalLifetimeSeconds = 60;
    private const int MaximumAckCount = 20;

    public static RouteGroupBuilder MapPeerSignaling(this RouteGroupBuilder api)
    {
        api.MapPost("/signal/send", SendSignal)
            .RequireRateLimiting("peer-operations");
        api.MapPost("/signal/fetch", FetchSignals)
            .RequireRateLimiting("peer-operations");
        api.MapPost("/signal/ack", AckSignals)
            .RequireRateLimiting("peer-operations");
        return api;
    }

    private static async Task<IResult> SendSignal(
        SendPeerSignalRequest request,
        IIdentityStore identities,
        ReplayNonceStore replayNonces,
        SignalStore signals,
        ICapabilityRevocationStore revocations,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!PeerId.TryParse(request.SenderPeerId, out var senderId) ||
            !PeerId.TryParse(request.TargetPeerId, out var targetId))
        {
            return BadRequest("invalid_peer_id", "SenderPeerId or TargetPeerId is invalid.");
        }

        if (senderId == targetId)
        {
            return BadRequest("invalid_target", "A peer cannot signal itself.");
        }

        var sender = await identities.GetAsync(senderId, cancellationToken);
        if (sender is null)
        {
            return Unauthorized("sender_unknown", "Sender is not a registered Dyract peer.");
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

        if (!IsHexId(request.SessionId))
        {
            return BadRequest("invalid_session_id", "SessionId must be a 128-bit lowercase/uppercase hexadecimal identifier.");
        }

        if (!PeerSignalTypes.IsSupported(request.SignalType))
        {
            return BadRequest("invalid_signal_type", "Signal type is not supported.");
        }

        if (request.Payload is null || Encoding.UTF8.GetByteCount(request.Payload) > MaximumPayloadBytes)
        {
            return BadRequest("invalid_signal_payload", $"Signal payload must not exceed {MaximumPayloadBytes} UTF-8 bytes.");
        }

        if ((request.SignalType is PeerSignalTypes.Offer or PeerSignalTypes.Answer or PeerSignalTypes.Candidate) &&
            string.IsNullOrWhiteSpace(request.Payload))
        {
            return BadRequest("invalid_signal_payload", "Offer, answer and candidate signals require a payload.");
        }

        var now = timeProvider.GetUtcNow();
        if (!TryValidateSignedRequestMetadata(request.TimestampUnixSeconds, request.Nonce, now, out var metadataError))
        {
            return metadataError!;
        }

        if (!TryUnixTime(request.SignalExpiresUnixSeconds, out var signalExpires) ||
            signalExpires <= now ||
            signalExpires > now.AddSeconds(MaximumSignalLifetimeSeconds))
        {
            return BadRequest("invalid_signal_expiry", $"Signal must expire within the next {MaximumSignalLifetimeSeconds} seconds.");
        }

        if (!TryDecodeBase64(request.Signature, 512, out var signature))
        {
            return BadRequest("invalid_signature", "Signature must be valid base64.");
        }

        byte[] proof;
        try
        {
            proof = ProofPayload.ForSignalSend(
                senderId.Value,
                targetId.Value,
                request.Capability.CapabilityId,
                request.SessionId,
                request.SignalType,
                request.Payload,
                request.SignalExpiresUnixSeconds,
                request.TimestampUnixSeconds,
                request.Nonce);
        }
        catch (ArgumentException exception)
        {
            return BadRequest("invalid_signal", exception.Message);
        }

        if (!SignatureVerifier.Verify(sender.PublicKey, proof, signature))
        {
            return Unauthorized("signature_invalid", "Signal signature could not be verified.");
        }

        var capabilityError = await ValidateContactCapabilityAsync(
            request.Capability,
            senderId,
            targetId,
            target.PublicKey,
            revocations,
            now,
            cancellationToken);
        if (capabilityError is not null)
        {
            return capabilityError;
        }

        if (!replayNonces.TryAccept(senderId, request.Nonce, now))
        {
            return Unauthorized("replay_detected", "This signed signal nonce has already been used.");
        }

        if (!signals.TryEnqueue(
                senderId,
                targetId,
                request.SessionId.ToLowerInvariant(),
                request.SignalType,
                request.Payload,
                now,
                signalExpires,
                out var stored))
        {
            return Results.Json(
                new ApiError("signal_inbox_full", "Target has too many pending signaling items."),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        return Results.Ok(new SendPeerSignalResponse(
            stored.SignalId,
            stored.ExpiresAt.ToUnixTimeSeconds()));
    }

    private static async Task<IResult> FetchSignals(
        FetchPeerSignalsRequest request,
        IIdentityStore identities,
        ReplayNonceStore replayNonces,
        SignalStore signals,
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

        var proof = ProofPayload.ForSignalFetch(peerId.Value, request.TimestampUnixSeconds, request.Nonce);
        if (!SignatureVerifier.Verify(identity.PublicKey, proof, signature))
        {
            return Unauthorized("signature_invalid", "Signal fetch signature could not be verified.");
        }

        if (!replayNonces.TryAccept(peerId, request.Nonce, now))
        {
            return Unauthorized("replay_detected", "This signed signal fetch nonce has already been used.");
        }

        var pending = signals.Fetch(peerId, now, SignalStore.MaximumFetchCount)
            .Select(signal => new PeerSignalEnvelope(
                signal.SignalId,
                signal.SenderPeerId.Value,
                signal.SessionId,
                signal.SignalType,
                signal.Payload,
                signal.CreatedAt.ToUnixTimeSeconds(),
                signal.ExpiresAt.ToUnixTimeSeconds()))
            .ToArray();

        return Results.Ok(new FetchPeerSignalsResponse(pending));
    }

    private static async Task<IResult> AckSignals(
        AckPeerSignalsRequest request,
        IIdentityStore identities,
        ReplayNonceStore replayNonces,
        SignalStore signals,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!PeerId.TryParse(request.PeerId, out var peerId))
        {
            return BadRequest("invalid_peer_id", "PeerId is invalid.");
        }

        if (request.SignalIds is not { Length: > 0 and <= MaximumAckCount } ||
            request.SignalIds.Distinct(StringComparer.Ordinal).Count() != request.SignalIds.Length ||
            request.SignalIds.Any(signalId => !IsHexId(signalId)))
        {
            return BadRequest("invalid_signal_ids", $"SignalIds must contain 1-{MaximumAckCount} unique 128-bit hexadecimal IDs.");
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

        byte[] proof;
        try
        {
            proof = ProofPayload.ForSignalAck(
                peerId.Value,
                request.SignalIds,
                request.TimestampUnixSeconds,
                request.Nonce);
        }
        catch (ArgumentException exception)
        {
            return BadRequest("invalid_signal_ids", exception.Message);
        }

        if (!SignatureVerifier.Verify(identity.PublicKey, proof, signature))
        {
            return Unauthorized("signature_invalid", "Signal acknowledgement signature could not be verified.");
        }

        if (!replayNonces.TryAccept(peerId, request.Nonce, now))
        {
            return Unauthorized("replay_detected", "This signed signal acknowledgement nonce has already been used.");
        }

        signals.Acknowledge(peerId, request.SignalIds, now);
        return Results.NoContent();
    }

    private static async Task<IResult?> ValidateContactCapabilityAsync(
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

        if (!IsHexId(capability.CapabilityId))
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

        return SignatureVerifier.Verify(targetPublicKey, capabilityProof, capabilitySignature)
            ? null
            : Unauthorized("capability_signature", "Contact capability signature could not be verified.");
    }

    private static bool TryValidateSignedRequestMetadata(
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

    private static bool TryDecodeBase64(string? value, int maximumBytes, out byte[] bytes)
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

    private static bool TryUnixTime(long unixSeconds, out DateTimeOffset value)
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

    private static bool IsHexId(string? value)
        => value is { Length: ContactCapabilityPolicy.CapabilityIdHexLength } && value.All(Uri.IsHexDigit);

    private static IResult BadRequest(string code, string message)
        => Results.BadRequest(new ApiError(code, message));

    private static IResult Unauthorized(string code, string message)
        => Results.Json(new ApiError(code, message), statusCode: StatusCodes.Status401Unauthorized);
}
