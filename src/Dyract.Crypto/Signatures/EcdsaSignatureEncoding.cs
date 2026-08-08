using System.Formats.Asn1;
using System.Security.Cryptography;

namespace Dyract.Crypto.Signatures;

public static class EcdsaSignatureEncoding
{
    private const int P256CoordinateLength = 32;

    public static byte[] DerToP256P1363(ReadOnlySpan<byte> derSignature)
    {
        if (derSignature.IsEmpty)
        {
            throw new CryptographicException("ECDSA signature must not be empty.");
        }

        try
        {
            var reader = new AsnReader(derSignature.ToArray(), AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            var r = sequence.ReadInteger();
            var s = sequence.ReadInteger();
            sequence.ThrowIfNotEmpty();
            reader.ThrowIfNotEmpty();

            if (r.Sign <= 0 || s.Sign <= 0)
            {
                throw new CryptographicException("ECDSA signature integers must be positive.");
            }

            var output = new byte[P256CoordinateLength * 2];
            WriteCoordinate(
                r.ToByteArray(isUnsigned: true, isBigEndian: true),
                output.AsSpan(0, P256CoordinateLength));
            WriteCoordinate(
                s.ToByteArray(isUnsigned: true, isBigEndian: true),
                output.AsSpan(P256CoordinateLength, P256CoordinateLength));
            return output;
        }
        catch (AsnContentException exception)
        {
            throw new CryptographicException("ECDSA signature is not canonical DER.", exception);
        }
    }

    private static void WriteCoordinate(ReadOnlySpan<byte> integer, Span<byte> destination)
    {
        if (integer.IsEmpty || integer.Length > destination.Length)
        {
            throw new CryptographicException("ECDSA signature coordinate is outside the P-256 range.");
        }

        destination.Clear();
        integer.CopyTo(destination[^integer.Length..]);
    }
}
