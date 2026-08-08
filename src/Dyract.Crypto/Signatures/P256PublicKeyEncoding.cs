using System.Security.Cryptography;

namespace Dyract.Crypto.Signatures;

public static class P256PublicKeyEncoding
{
    private const int CoordinateLength = 32;
    private const int UncompressedPointLength = 1 + (CoordinateLength * 2);

    public static byte[] UncompressedPointToSubjectPublicKeyInfo(ReadOnlySpan<byte> point)
    {
        if (point.Length != UncompressedPointLength || point[0] != 0x04)
        {
            throw new CryptographicException(
                "P-256 public key must be a 65-byte uncompressed X9.63 point beginning with 0x04.");
        }

        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = point.Slice(1, CoordinateLength).ToArray(),
                Y = point.Slice(1 + CoordinateLength, CoordinateLength).ToArray()
            }
        };

        using var key = ECDsa.Create(parameters);
        var spki = key.ExportSubjectPublicKeyInfo();
        if (!SignatureVerifier.IsValidIdentityPublicKey(spki))
        {
            CryptographicOperations.ZeroMemory(spki);
            throw new CryptographicException("P-256 public point could not be encoded as a valid Dyract identity key.");
        }

        return spki;
    }
}
