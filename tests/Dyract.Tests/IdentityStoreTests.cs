using Dyract.Crypto.Identity;
using Dyract.Server.Services;
using Xunit;

namespace Dyract.Tests;

public sealed class IdentityStoreTests
{
    [Fact]
    public async Task RegisteringSameIdentity_IsIdempotent()
    {
        IIdentityStore store = new InMemoryIdentityStore();
        using var identity = PeerIdentity.Generate();
        var publicKey = identity.ExportPublicKey();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        var first = await store.RegisterAsync(identity.PeerId, publicKey, now);
        var second = await store.RegisterAsync(identity.PeerId, publicKey, now.AddMinutes(1));

        Assert.Equal(IdentityRegistrationStatus.Created, first.Status);
        Assert.Equal(IdentityRegistrationStatus.Existing, second.Status);
        Assert.Equal(first.Peer, second.Peer);
        Assert.Equal(now, second.Peer.RegisteredAt);
    }

    [Fact]
    public async Task UnknownIdentity_IsNotReturned()
    {
        IIdentityStore store = new InMemoryIdentityStore();
        using var identity = PeerIdentity.Generate();

        var peer = await store.GetAsync(identity.PeerId);

        Assert.Null(peer);
    }
}
