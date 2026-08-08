using Dyract.Crypto.Identity;
using Dyract.Server.Services;
using Xunit;

namespace Dyract.Tests;

public sealed class SignalStoreTests
{
    [Fact]
    public async Task ExpiredSignals_AreNotReturned()
    {
        var store = new SignalStore();
        using var sender = PeerIdentity.Generate();
        using var target = PeerIdentity.Generate();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        var signal = await store.TryEnqueueAsync(
            sender.PeerId,
            target.PeerId,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "offer",
            "payload",
            now,
            now.AddSeconds(10));

        Assert.NotNull(signal);
        Assert.Empty(await store.FetchAsync(target.PeerId, now.AddSeconds(11)));
    }

    [Fact]
    public async Task Acknowledgement_CannotRemoveAnotherTargetsSignal()
    {
        var store = new SignalStore();
        using var sender = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        using var charlie = PeerIdentity.Generate();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        var signal = await store.TryEnqueueAsync(
            sender.PeerId,
            bob.PeerId,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "candidate",
            "payload",
            now,
            now.AddSeconds(30));

        Assert.NotNull(signal);
        Assert.Equal(0, await store.AcknowledgeAsync(charlie.PeerId, new[] { signal.SignalId }, now));
        Assert.Single(await store.FetchAsync(bob.PeerId, now));
    }

    [Fact]
    public async Task PendingInbox_IsBounded()
    {
        var store = new SignalStore();
        using var sender = PeerIdentity.Generate();
        using var target = PeerIdentity.Generate();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        for (var i = 0; i < SignalStore.MaximumPendingPerPeer; i++)
        {
            Assert.NotNull(await store.TryEnqueueAsync(
                sender.PeerId,
                target.PeerId,
                $"{i:x32}",
                "candidate",
                $"candidate-{i}",
                now,
                now.AddSeconds(30)));
        }

        Assert.Null(await store.TryEnqueueAsync(
            sender.PeerId,
            target.PeerId,
            "ffffffffffffffffffffffffffffffff",
            "candidate",
            "overflow",
            now,
            now.AddSeconds(30)));
    }
}
