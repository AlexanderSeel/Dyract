using Dyract.Client;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Xunit;

namespace Dyract.Tests;

public sealed class ContactCapabilityPolicyTests
{
    [Fact]
    public void Factory_RejectsCapabilityLifetimeBeyondMaximum()
    {
        using var issuer = PeerIdentity.Generate();
        using var grantee = PeerIdentity.Generate();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ContactCapabilityFactory.Create(
                issuer,
                grantee.PeerId.Value,
                ContactCapabilityPolicy.MaximumLifetime + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Verifier_RejectsCorrectlySignedCapabilityBeyondMaximumLifetime()
    {
        using var issuer = PeerIdentity.Generate();
        using var grantee = PeerIdentity.Generate();
        var now = DateTimeOffset.UtcNow;
        var issued = now.ToUnixTimeSeconds();
        var expires = now.Add(ContactCapabilityPolicy.MaximumLifetime).AddMinutes(1).ToUnixTimeSeconds();
        var capabilityId = new string('a', ContactCapabilityPolicy.CapabilityIdHexLength);
        var proof = ProofPayload.ForContactCapability(
            issuer.PeerId.Value,
            grantee.PeerId.Value,
            capabilityId,
            issued,
            expires);
        var capability = new ContactCapability(
            Version: 1,
            IssuerPeerId: issuer.PeerId.Value,
            GranteePeerId: grantee.PeerId.Value,
            CapabilityId: capabilityId,
            IssuedUnixSeconds: issued,
            ExpiresUnixSeconds: expires,
            Signature: Convert.ToBase64String(issuer.Sign(proof)));

        Assert.False(ContactCapabilityVerifier.TryVerify(
            capability,
            issuer.ExportPublicKey(),
            grantee.PeerId.Value,
            out var error));
        Assert.Contains("lifetime", error, StringComparison.OrdinalIgnoreCase);
    }
}
