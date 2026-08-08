using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Dyract.Server.Services;
using StackExchange.Redis;
using Xunit;

namespace Dyract.Tests;

public sealed class RedisTransientStateTests
{
    private const string ConnectionEnvironmentVariable = "DYRACT_REDIS_TEST_CONNECTION";

    [Fact]
    public async Task Presence_IsSharedAcrossStoreInstancesAndCanBeRemoved()
    {
        var connectionString = GetConnectionStringOrSkip();
        if (connectionString is null)
        {
            return;
        }

        using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var prefix = $"dyract:test:{Guid.NewGuid():N}";
        var first = new RedisPresenceStore(connection, prefix);
        var second = new RedisPresenceStore(connection, prefix);
        using var identity = PeerIdentity.Generate();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddSeconds(90);
        var candidate = new ConnectionCandidate("srflx", "udp", "203.0.113.25", 41000, 100);

        await first.PublishAsync(identity.PeerId, new[] { candidate }, now, expiresAt);

        var fromSecondInstance = await second.GetAsync(identity.PeerId, now.AddSeconds(1));
        Assert.NotNull(fromSecondInstance);
        Assert.Equal(identity.PeerId, fromSecondInstance.PeerId);
        Assert.Equal(expiresAt.ToUnixTimeSeconds(), fromSecondInstance.ExpiresAt.ToUnixTimeSeconds());
        Assert.Single(fromSecondInstance.Candidates);
        Assert.Equal(candidate, fromSecondInstance.Candidates[0]);

        Assert.True(await second.RemoveAsync(identity.PeerId));
        Assert.Null(await first.GetAsync(identity.PeerId, now.AddSeconds(2)));
    }

    [Fact]
    public async Task Presence_LogicalExpiryFailsClosedAndDeletesStoredLease()
    {
        var connectionString = GetConnectionStringOrSkip();
        if (connectionString is null)
        {
            return;
        }

        using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var prefix = $"dyract:test:{Guid.NewGuid():N}";
        var first = new RedisPresenceStore(connection, prefix);
        var second = new RedisPresenceStore(connection, prefix);
        using var identity = PeerIdentity.Generate();
        var now = DateTimeOffset.UtcNow;

        await first.PublishAsync(
            identity.PeerId,
            new[] { new ConnectionCandidate("host", "udp", "192.168.50.20", 42000, 10) },
            now,
            now.AddSeconds(30));

        Assert.Null(await second.GetAsync(identity.PeerId, now.AddSeconds(31)));
        Assert.False(await first.RemoveAsync(identity.PeerId));
    }

    [Fact]
    public async Task ReplayNonce_IsSharedAcrossStoreInstancesAndPeerScoped()
    {
        var connectionString = GetConnectionStringOrSkip();
        if (connectionString is null)
        {
            return;
        }

        using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var prefix = $"dyract:test:{Guid.NewGuid():N}";
        var first = new RedisReplayNonceStore(connection, prefix);
        var second = new RedisReplayNonceStore(connection, prefix);
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        var now = DateTimeOffset.UtcNow;
        var nonce = Convert.ToBase64String(new byte[24]);

        Assert.True(await first.TryAcceptAsync(alice.PeerId, nonce, now));
        Assert.False(await second.TryAcceptAsync(alice.PeerId, nonce, now.AddSeconds(1)));
        Assert.True(await second.TryAcceptAsync(bob.PeerId, nonce, now.AddSeconds(1)));
        Assert.True(await second.TryAcceptAsync(alice.PeerId, Convert.ToBase64String(new byte[25]), now.AddSeconds(1)));
    }

    private static string? GetConnectionStringOrSkip()
        => Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
}
