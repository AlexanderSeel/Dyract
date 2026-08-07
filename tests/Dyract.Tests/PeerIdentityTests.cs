using Dyract.Core.Identity;
using Dyract.Crypto.Identity;
using Dyract.Crypto.Signatures;
using Xunit;

namespace Dyract.Tests;

public sealed class PeerIdentityTests
{
    [Fact]
    public void PeerId_IsDerivedFromPublicKey()
    {
        using var identity = PeerIdentity.Generate();
        var publicKey = identity.ExportPublicKey();

        var derived = PeerId.FromPublicKey(publicKey);

        Assert.Equal(identity.PeerId, derived);
        Assert.StartsWith(PeerId.Prefix, derived.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportAndImportPrivateKey_PreservesIdentity()
    {
        using var identity = PeerIdentity.Generate();
        var privateKey = identity.ExportPkcs8PrivateKey();
        using var imported = PeerIdentity.ImportPkcs8PrivateKey(privateKey);

        Assert.Equal(identity.PeerId, imported.PeerId);
        Assert.Equal(identity.ExportPublicKey(), imported.ExportPublicKey());
    }

    [Fact]
    public void Signature_VerifiesAndRejectsModifiedPayload()
    {
        using var identity = PeerIdentity.Generate();
        var payload = "dyract-test-payload"u8.ToArray();
        var signature = identity.Sign(payload);

        Assert.True(SignatureVerifier.Verify(identity.ExportPublicKey(), payload, signature));

        payload[0] ^= 0x01;
        Assert.False(SignatureVerifier.Verify(identity.ExportPublicKey(), payload, signature));
    }
}
