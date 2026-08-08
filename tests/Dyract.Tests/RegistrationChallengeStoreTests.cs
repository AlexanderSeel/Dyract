using Dyract.Crypto.Identity;
using Dyract.Server.Services;
using Xunit;

namespace Dyract.Tests;

public sealed class RegistrationChallengeStoreTests
{
    [Fact]
    public async Task Challenge_IsReadableOnceAndCanBeConsumed()
    {
        var store = new RegistrationChallengeStore();
        using var identity = PeerIdentity.Generate();
        var now = DateTimeOffset.UtcNow;
        var publicKey = identity.ExportPublicKey();

        var created = await store.CreateAsync(identity.PeerId, publicKey, now);
        var fetched = await store.GetAsync(created.Id, now.AddSeconds(1));

        Assert.NotNull(fetched);
        Assert.Equal(identity.PeerId, fetched.PeerId);
        Assert.Equal(publicKey, fetched.PublicKey);
        Assert.Equal(created.ChallengeBytes, fetched.ChallengeBytes);
        Assert.True(await store.TryConsumeAsync(created.Id));
        Assert.Null(await store.GetAsync(created.Id, now.AddSeconds(2)));
        Assert.False(await store.TryConsumeAsync(created.Id));
    }

    [Fact]
    public async Task Challenge_ExpiresFailClosed()
    {
        var store = new RegistrationChallengeStore();
        using var identity = PeerIdentity.Generate();
        var now = DateTimeOffset.UtcNow;

        var created = await store.CreateAsync(identity.PeerId, identity.ExportPublicKey(), now);

        Assert.Null(await store.GetAsync(
            created.Id,
            now.Add(RegistrationChallengeStore.Lifetime).AddSeconds(1)));
        Assert.False(await store.TryConsumeAsync(created.Id));
    }
}
