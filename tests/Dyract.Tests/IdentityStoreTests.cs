using Dyract.Core.Identity;
using Dyract.Crypto.Identity;
using Dyract.Server.Services;
using Xunit;

namespace Dyract.Tests;

public sealed class IdentityStoreTests
{
    [Fact]
    public void RegisteringSameIdentity_IsIdempotent()
    {
        IIdentityStore store = new InMemoryIdentityStore();
        using var identity = PeerIdentity.Generate();
        var publicKey = identity.ExportPublicKey();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        Assert.True(store.TryRegister(identity.PeerId, publicKey, now, out var first));
        Assert.True(store.TryRegister(identity.PeerId, publicKey, now.AddMinutes(1), out var second));

        Assert.Equal(first, second);
        Assert.Equal(now, second.RegisteredAt);
    }

    [Fact]
    public void UnknownIdentity_IsNotReturned()
    {
        IIdentityStore store = new InMemoryIdentityStore();
        using var identity = PeerIdentity.Generate();

        Assert.False(store.TryGet(identity.PeerId, out _));
    }
}
