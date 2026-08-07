using System.Security.Cryptography;

namespace Dyract.Crypto.Signatures;

public static class SignatureVerifier
{
    public static bool IsValidIdentityPublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.IsEmpty)
        {
            return false;
        }

        try
        {
            using var key = Import(publicKey);
            var parameters = key.ExportParameters(false);
            return string.Equals(
                parameters.Curve.Oid.Value,
                ECCurve.NamedCurves.nistP256.Oid.Value,
                StringComparison.Ordinal);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static bool Verify(
        ReadOnlySpan<byte> publicKey,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> signature)
    {
        if (publicKey.IsEmpty || payload.IsEmpty || signature.IsEmpty)
        {
            return false;
        }

        try
        {
            using var key = Import(publicKey);
            return key.VerifyData(
                payload,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static ECDsa Import(ReadOnlySpan<byte> publicKey)
    {
        var key = ECDsa.Create();

        try
        {
            key.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            if (bytesRead != publicKey.Length)
            {
                throw new CryptographicException("Unexpected trailing public-key data.");
            }

            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }
}
