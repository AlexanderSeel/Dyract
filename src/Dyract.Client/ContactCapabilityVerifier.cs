using Dyract.Core.Identity;
using Dyract.Crypto.Signatures;
using Dyract.Protocol;

namespace Dyract.Client;

public static class ContactCapabilityVerifier
{
    public static bool TryVerify(
        ContactCapability capability,
        ReadOnlySpan<byte> issuerPublicKey,
        string expectedGranteePeerId,
        out string? error,
        TimeProvider? timeProvider = null)
    {
        error = null;

        try
        {
            ContactPairingCodec.ValidateStructure(capability);
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }

        if (!PeerId.TryParse(expectedGranteePeerId, out var grantee))
        {
            error = "Local grantee PeerId is invalid.";
            return false;
        }

        PeerId issuer;
        try
        {
            issuer = PeerId.FromPublicKey(issuerPublicKey);
        }
        catch (ArgumentException)
        {
            error = "Pinned contact public key is invalid.";
            return false;
        }

        if (!string.Equals(capability.IssuerPeerId, issuer.Value, StringComparison.Ordinal))
        {
            error = "Pairing response was not issued by this saved contact.";
            return false;
        }

        if (!string.Equals(capability.GranteePeerId, grantee.Value, StringComparison.Ordinal))
        {
            error = "Pairing response was issued for a different Dyract identity.";
            return false;
        }

        if (!ContactCapabilityPolicy.IsLifetimeAllowed(
                capability.IssuedUnixSeconds,
                capability.ExpiresUnixSeconds))
        {
            error = "Pairing response lifetime is invalid or exceeds the supported maximum.";
            return false;
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        DateTimeOffset issuedAt;
        DateTimeOffset expiresAt;

        try
        {
            issuedAt = DateTimeOffset.FromUnixTimeSeconds(capability.IssuedUnixSeconds);
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(capability.ExpiresUnixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            error = "Pairing response timestamps are outside the supported range.";
            return false;
        }

        if (issuedAt > now.AddMinutes(2))
        {
            error = "Pairing response appears to have been issued in the future.";
            return false;
        }

        if (expiresAt <= now)
        {
            error = "Pairing response has expired.";
            return false;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(capability.Signature);
        }
        catch (FormatException)
        {
            error = "Pairing response signature is invalid.";
            return false;
        }

        var proof = ProofPayload.ForContactCapability(
            capability.IssuerPeerId,
            capability.GranteePeerId,
            capability.CapabilityId,
            capability.IssuedUnixSeconds,
            capability.ExpiresUnixSeconds);

        if (!SignatureVerifier.Verify(issuerPublicKey, proof, signature))
        {
            error = "Pairing response signature could not be verified against the saved contact key.";
            return false;
        }

        return true;
    }
}
