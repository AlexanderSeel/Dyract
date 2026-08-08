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

    [Fact]
    public async Task Signaling_IsSharedNonDestructiveAndAckedAcrossInstances()
    {
        var connectionString = GetConnectionStringOrSkip();
        if (connectionString is null)
        {
            return;
        }

        using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var prefix = $"dyract:test:{Guid.NewGuid():N}";
        var first = new RedisSignalStore(connection, prefix);
        var second = new RedisSignalStore(connection, prefix);
        using var sender = PeerIdentity.Generate();
        using var target = PeerIdentity.Generate();
        var now = DateTimeOffset.UtcNow;

        var signal = await first.TryEnqueueAsync(
            sender.PeerId,
            target.PeerId,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "offer",
            "{\"type\":\"offer\"}",
            now,
            now.AddSeconds(45));

        Assert.NotNull(signal);

        var firstFetch = await second.FetchAsync(target.PeerId, now.AddSeconds(1));
        Assert.Single(firstFetch);
        Assert.Equal(signal.SignalId, firstFetch[0].SignalId);
        Assert.Equal(sender.PeerId, firstFetch[0].SenderPeerId);
        Assert.Equal("{\"type\":\"offer\"}", firstFetch[0].Payload);

        var secondFetch = await first.FetchAsync(target.PeerId, now.AddSeconds(2));
        Assert.Single(secondFetch);
        Assert.Equal(signal.SignalId, secondFetch[0].SignalId);

        Assert.Equal(1, await second.AcknowledgeAsync(target.PeerId, new[] { signal.SignalId }, now.AddSeconds(3)));
        Assert.Empty(await first.FetchAsync(target.PeerId, now.AddSeconds(4)));
    }

    [Fact]
    public async Task Signaling_ExpiryAndTargetScopedAckAreEnforcedAcrossInstances()
    {
        var connectionString = GetConnectionStringOrSkip();
        if (connectionString is null)
        {
            return;
        }

        using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var prefix = $"dyract:test:{Guid.NewGuid():N}";
        var first = new RedisSignalStore(connection, prefix);
        var second = new RedisSignalStore(connection, prefix);
        using var sender = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        using var charlie = PeerIdentity.Generate();
        var now = DateTimeOffset.UtcNow;

        var signal = await first.TryEnqueueAsync(
            sender.PeerId,
            bob.PeerId,
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "candidate",
            "candidate",
            now,
            now.AddSeconds(10));
        Assert.NotNull(signal);

        Assert.Equal(0, await second.AcknowledgeAsync(charlie.PeerId, new[] { signal.SignalId }, now.AddSeconds(1)));
        Assert.Single(await second.FetchAsync(bob.PeerId, now.AddSeconds(2)));
        Assert.Empty(await first.FetchAsync(bob.PeerId, now.AddSeconds(11)));
    }

    [Fact]
    public async Task Signaling_PerTargetCapacityIsAtomicAcrossStoreInstances()
    {
        var connectionString = GetConnectionStringOrSkip();
        if (connectionString is null)
        {
            return;
        }

        using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var prefix = $"dyract:test:{Guid.NewGuid():N}";
        var first = new RedisSignalStore(connection, prefix);
        var second = new RedisSignalStore(connection, prefix);
        using var sender = PeerIdentity.Generate();
        using var target = PeerIdentity.Generate();
        var now = DateTimeOffset.UtcNow;
        var acceptedIds = new List<string>();

        for (var index = 0; index < SignalStore.MaximumPendingPerPeer; index++)
        {
            var store = index % 2 == 0 ? first : second;
            var signal = await store.TryEnqueueAsync(
                sender.PeerId,
                target.PeerId,
                $"{index:x32}",
                "candidate",
                $"candidate-{index}",
                now.AddMilliseconds(index),
                now.AddSeconds(50));
            Assert.NotNull(signal);
            acceptedIds.Add(signal.SignalId);
        }

        Assert.Null(await second.TryEnqueueAsync(
            sender.PeerId,
            target.PeerId,
            "ffffffffffffffffffffffffffffffff",
            "candidate",
            "overflow",
            now.AddSeconds(1),
            now.AddSeconds(50)));

        var fetched = await first.FetchAsync(target.PeerId, now.AddSeconds(2));
        Assert.Equal(SignalStore.MaximumFetchCount, fetched.Count);

        for (var offset = 0; offset < acceptedIds.Count; offset += SignalStore.MaximumFetchCount)
        {
            var batch = acceptedIds.Skip(offset).Take(SignalStore.MaximumFetchCount).ToArray();
            await second.AcknowledgeAsync(target.PeerId, batch, now.AddSeconds(3));
        }

        Assert.Empty(await first.FetchAsync(target.PeerId, now.AddSeconds(4)));
    }

    private static string? GetConnectionStringOrSkip()
        => Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
}
