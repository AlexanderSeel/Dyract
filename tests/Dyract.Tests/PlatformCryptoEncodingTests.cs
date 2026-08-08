using System.Security.Cryptography;
using Dyract.Core.Identity;
using Dyract.Crypto.Signatures;
using Xunit;

namespace Dyract.Tests;

public sealed class PlatformCryptoEncodingTests
{
    [Fact]
    public void DerEcdsaSignature_ConvertsToDyractP1363AndVerifies()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = "platform-signature-encoding"u8.ToArray();
        var der = key.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);

        var p1363 = EcdsaSignatureEncoding.DerToP256P1363(der);

        Assert.Equal(64, p1363.Length);
        Assert.True(key.VerifyData(
            payload,
            p1363,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Theory]
    [InlineData("")]
    [InlineData("3000")]
    [InlineData("3006020100020101")]
    public void DerEcdsaSignature_InvalidEncodingFailsClosed(string hex)
    {
        var bytes = hex.Length == 0 ? Array.Empty<byte>() : Convert.FromHexString(hex);
        Assert.Throws<CryptographicException>(() => EcdsaSignatureEncoding.DerToP256P1363(bytes));
    }

    [Fact]
    public void RawP256Point_ConvertsToSamePeerIdAsOriginalSpki()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(false);
        Assert.NotNull(parameters.Q.X);
        Assert.NotNull(parameters.Q.Y);

        var point = new byte[65];
        point[0] = 0x04;
        parameters.Q.X.CopyTo(point, 1);
        parameters.Q.Y.CopyTo(point, 33);

        var originalSpki = key.ExportSubjectPublicKeyInfo();
        var convertedSpki = P256PublicKeyEncoding.UncompressedPointToSubjectPublicKeyInfo(point);

        Assert.Equal(PeerId.FromPublicKey(originalSpki), PeerId.FromPublicKey(convertedSpki));
        Assert.True(SignatureVerifier.IsValidIdentityPublicKey(convertedSpki));
    }

    [Theory]
    [InlineData(64, 4)]
    [InlineData(65, 3)]
    [InlineData(66, 4)]
    public void RawP256Point_InvalidShapeFailsClosed(int length, byte prefix)
    {
        var point = new byte[length];
        if (length > 0)
        {
            point[0] = prefix;
        }

        Assert.Throws<CryptographicException>(() =>
            P256PublicKeyEncoding.UncompressedPointToSubjectPublicKeyInfo(point));
    }
}
