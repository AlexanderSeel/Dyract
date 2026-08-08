using Dyract.Crypto.Identity;
using Dyract.Server.Services;
using Xunit;

namespace Dyract.Tests;

public sealed class CapabilityRevocationStoreTests
{
    [Fact]
    public void Revoke_IsIssuerScopedAndExpiresNaturally()
    {
        var store = new CapabilityRevocationStore();
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        var now = DateTimeOffset.UtcNow;
        var id = new string('a', 32);

        Assert.Equal(
            CapabilityRevocationResult.Revoked,
            store.Revoke(alice.PeerId, id, now.AddMinutes(10), now));
        Assert.True(store.IsRevoked(alice.PeerId, id, now));
        Assert.False(store.IsRevoked(bob.PeerId, id, now));
        Assert.False(store.IsRevoked(alice.PeerId, id, now.AddMinutes(11)));
        Assert.Equal(0, store.CountActive(alice.PeerId, now.AddMinutes(11)));
    }

    [Fact]
    public void Revoke_IsIdempotentAndCanExtendSameRevocationToNaturalExpiry()
    {
        var store = new CapabilityRevocationStore();
        using var issuer = PeerIdentity.Generate();
        var now = DateTimeOffset.UtcNow;
        var id = new string('b', 32);

        Assert.Equal(
            CapabilityRevocationResult.Revoked,
            store.Revoke(issuer.PeerId, id, now.AddMinutes(5), now));
        Assert.Equal(
            CapabilityRevocationResult.AlreadyRevoked,
            store.Revoke(issuer.PeerId, id, now.AddMinutes(10), now));
        Assert.True(store.IsRevoked(issuer.PeerId, id, now.AddMinutes(7)));
    }

    [Fact]
    public void Revoke_RejectsNewEntriesAtPerIssuerCapacity()
    {
        var store = new CapabilityRevocationStore();
        using var issuer = PeerIdentity.Generate();
        var now = DateTimeOffset.UtcNow;

        for (var index = 0; index < CapabilityRevocationStore.MaximumActiveRevocationsPerIssuer; index++)
        {
            var id = index.ToString("x32");
            Assert.Equal(
                CapabilityRevocationResult.Revoked,
                store.Revoke(issuer.PeerId, id, now.AddDays(1), now));
        }

        Assert.Equal(
            CapabilityRevocationResult.CapacityExceeded,
            store.Revoke(issuer.PeerId, new string('f', 32), now.AddDays(1), now));
    }
}
