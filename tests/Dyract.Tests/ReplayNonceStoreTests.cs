using Dyract.Crypto.Identity;
using Dyract.Server.Services;
using Xunit;

namespace Dyract.Tests;

public sealed class ReplayNonceStoreTests
{
    [Fact]
    public async Task SamePeerAndNonce_IsAcceptedOnlyOnceUntilExpiry()
    {
        var store = new ReplayNonceStore();
        using var identity = PeerIdentity.Generate();
        var now = DateTimeOffset.UtcNow;
        var nonce = Convert.ToBase64String(new byte[24]);

        Assert.True(await store.TryAcceptAsync(identity.PeerId, nonce, now));
        Assert.False(await store.TryAcceptAsync(identity.PeerId, nonce, now.AddMinutes(1)));
        Assert.True(await store.TryAcceptAsync(identity.PeerId, nonce, now.Add(ReplayNonceStore.Lifetime).AddSeconds(1)));
    }

    [Fact]
    public async Task SameNonce_IsScopedToPeer()
    {
        var store = new ReplayNonceStore();
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        var now = DateTimeOffset.UtcNow;
        var nonce = Convert.ToBase64String(new byte[24]);

        Assert.True(await store.TryAcceptAsync(alice.PeerId, nonce, now));
        Assert.True(await store.TryAcceptAsync(bob.PeerId, nonce, now));
    }
}
