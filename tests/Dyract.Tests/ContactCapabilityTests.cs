using Dyract.Client;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Xunit;

namespace Dyract.Tests;

public sealed class ContactCapabilityTests
{
    [Fact]
    public void Capability_IsBoundToIssuerAndGrantee()
    {
        using var issuer = PeerIdentity.Generate();
        using var grantee = PeerIdentity.Generate();

        var capability = ContactCapabilityFactory.Create(
            issuer,
            grantee.PeerId.Value,
            TimeSpan.FromHours(1));

        Assert.Equal(1, capability.Version);
        Assert.Equal(issuer.PeerId.Value, capability.IssuerPeerId);
        Assert.Equal(grantee.PeerId.Value, capability.GranteePeerId);
        Assert.Equal(32, capability.CapabilityId.Length);

        var proof = ProofPayload.ForContactCapability(
            capability.IssuerPeerId,
            capability.GranteePeerId,
            capability.CapabilityId,
            capability.IssuedUnixSeconds,
            capability.ExpiresUnixSeconds);

        Assert.True(issuer.Verify(proof, Convert.FromBase64String(capability.Signature)));
    }

    [Fact]
    public void CapabilitySignature_FailsWhenGranteeIsChanged()
    {
        using var issuer = PeerIdentity.Generate();
        using var grantee = PeerIdentity.Generate();
        using var attacker = PeerIdentity.Generate();

        var capability = ContactCapabilityFactory.Create(
            issuer,
            grantee.PeerId.Value,
            TimeSpan.FromHours(1));

        var tamperedProof = ProofPayload.ForContactCapability(
            capability.IssuerPeerId,
            attacker.PeerId.Value,
            capability.CapabilityId,
            capability.IssuedUnixSeconds,
            capability.ExpiresUnixSeconds);

        Assert.False(issuer.Verify(tamperedProof, Convert.FromBase64String(capability.Signature)));
    }
}
