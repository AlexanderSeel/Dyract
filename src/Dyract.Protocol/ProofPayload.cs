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

    private static void ValidateField(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        if (value.Contains('\n') || value.Contains('\r'))
        {
            throw new ArgumentException("Signed proof fields must not contain line breaks.", parameterName);
        }
    }
}
