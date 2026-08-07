using System.Security.Cryptography;
using System.Text;

namespace Dyract.Protocol;

public static class ProofPayload
{
    public static byte[] ForRegistration(
        string challengeId,
        string peerId,
        string publicKey,
        string challenge)
    {
        ValidateField(challengeId, nameof(challengeId));
        ValidateField(peerId, nameof(peerId));
        ValidateField(publicKey, nameof(publicKey));
        ValidateField(challenge, nameof(challenge));

        return Encoding.UTF8.GetBytes(
            $"dyract:register:v1\n{challengeId}\n{peerId}\n{publicKey}\n{challenge}");
    }

    public static byte[] ForLookup(
        string requesterPeerId,
        string targetPeerId,
        long timestampUnixSeconds,
        string nonce)
    {
        ValidateField(requesterPeerId, nameof(requesterPeerId));
        ValidateField(targetPeerId, nameof(targetPeerId));
        ValidateField(nonce, nameof(nonce));

        return Encoding.UTF8.GetBytes(
            $"dyract:lookup:v1\n{requesterPeerId}\n{targetPeerId}\n{timestampUnixSeconds}\n{nonce}");
    }

    public static byte[] ForContactCapability(
        string issuerPeerId,
        string granteePeerId,
        string capabilityId,
        long issuedUnixSeconds,
        long expiresUnixSeconds)
    {
        ValidateField(issuerPeerId, nameof(issuerPeerId));
        ValidateField(granteePeerId, nameof(granteePeerId));
        ValidateField(capabilityId, nameof(capabilityId));

        return Encoding.UTF8.GetBytes(
            $"dyract:contact-capability:v1\n{issuerPeerId}\n{granteePeerId}\n{capabilityId}\n{issuedUnixSeconds}\n{expiresUnixSeconds}");
    }

    public static byte[] ForPresence(
        string peerId,
        IReadOnlyList<ConnectionCandidate> candidates,
        long leaseExpiresUnixSeconds,
        long timestampUnixSeconds,
        string nonce)
    {
        ValidateField(peerId, nameof(peerId));
        ValidateField(nonce, nameof(nonce));
        ArgumentNullException.ThrowIfNull(candidates);

        var builder = new StringBuilder()
            .Append("dyract:presence:v1\n")
            .Append(peerId).Append('\n')
            .Append(leaseExpiresUnixSeconds).Append('\n')
            .Append(timestampUnixSeconds).Append('\n')
            .Append(nonce).Append('\n')
            .Append(candidates.Count);

        foreach (var candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ValidateStructuredField(candidate.Kind, nameof(candidate.Kind));
            ValidateStructuredField(candidate.Protocol, nameof(candidate.Protocol));
            ValidateStructuredField(candidate.Address, nameof(candidate.Address));

            builder.Append('\n')
                .Append(candidate.Kind).Append('\t')
                .Append(candidate.Protocol).Append('\t')
                .Append(candidate.Address).Append('\t')
                .Append(candidate.Port).Append('\t')
                .Append(candidate.Priority);
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public static byte[] ForPresenceRemoval(
        string peerId,
        long timestampUnixSeconds,
        string nonce)
    {
        ValidateField(peerId, nameof(peerId));
        ValidateField(nonce, nameof(nonce));

        return Encoding.UTF8.GetBytes(
            $"dyract:presence-remove:v1\n{peerId}\n{timestampUnixSeconds}\n{nonce}");
    }

    public static byte[] ForResolve(
        string requesterPeerId,
        string targetPeerId,
        string capabilityId,
        long timestampUnixSeconds,
        string nonce)
    {
        ValidateField(requesterPeerId, nameof(requesterPeerId));
        ValidateField(targetPeerId, nameof(targetPeerId));
        ValidateField(capabilityId, nameof(capabilityId));
        ValidateField(nonce, nameof(nonce));

        return Encoding.UTF8.GetBytes(
            $"dyract:resolve:v1\n{requesterPeerId}\n{targetPeerId}\n{capabilityId}\n{timestampUnixSeconds}\n{nonce}");
    }

    public static byte[] ForSignalSend(
        string senderPeerId,
        string targetPeerId,
        string capabilityId,
        string sessionId,
        string signalType,
        string payload,
        long signalExpiresUnixSeconds,
        long timestampUnixSeconds,
        string nonce)
    {
        ValidateField(senderPeerId, nameof(senderPeerId));
        ValidateField(targetPeerId, nameof(targetPeerId));
        ValidateField(capabilityId, nameof(capabilityId));
        ValidateField(sessionId, nameof(sessionId));
        ValidateField(signalType, nameof(signalType));
        ValidateField(nonce, nameof(nonce));
        ArgumentNullException.ThrowIfNull(payload);

        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var payloadHash = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();

        return Encoding.UTF8.GetBytes(
            $"dyract:signal-send:v1\n{senderPeerId}\n{targetPeerId}\n{capabilityId}\n{sessionId}\n{signalType}\n{payloadHash}\n{signalExpiresUnixSeconds}\n{timestampUnixSeconds}\n{nonce}");
    }

    public static byte[] ForSignalFetch(
        string peerId,
        long timestampUnixSeconds,
        string nonce)
    {
        ValidateField(peerId, nameof(peerId));
        ValidateField(nonce, nameof(nonce));

        return Encoding.UTF8.GetBytes(
            $"dyract:signal-fetch:v1\n{peerId}\n{timestampUnixSeconds}\n{nonce}");
    }

    public static byte[] ForSignalAck(
        string peerId,
        IReadOnlyCollection<string> signalIds,
        long timestampUnixSeconds,
        string nonce)
    {
        ValidateField(peerId, nameof(peerId));
        ValidateField(nonce, nameof(nonce));
        ArgumentNullException.ThrowIfNull(signalIds);

        var orderedIds = signalIds.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (orderedIds.Length == 0)
        {
            throw new ArgumentException("At least one signal ID is required.", nameof(signalIds));
        }

        var builder = new StringBuilder()
            .Append("dyract:signal-ack:v1\n")
            .Append(peerId).Append('\n')
            .Append(timestampUnixSeconds).Append('\n')
            .Append(nonce).Append('\n')
            .Append(orderedIds.Length);

        foreach (var signalId in orderedIds)
        {
            ValidateField(signalId, nameof(signalIds));
            builder.Append('\n').Append(signalId);
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void ValidateField(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        if (value.Contains('\n') || value.Contains('\r'))
        {
            throw new ArgumentException("Signed proof fields must not contain line breaks.", parameterName);
        }
    }

    private static void ValidateStructuredField(string value, string parameterName)
    {
        ValidateField(value, parameterName);

        if (value.Contains('\t'))
        {
            throw new ArgumentException("Structured signed proof fields must not contain tab characters.", parameterName);
        }
    }
}
