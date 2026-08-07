using Dyract.Crypto.Identity;
using Dyract.Server.Services;
using Xunit;

namespace Dyract.Tests;

public sealed class SignalStoreTests
{
    [Fact]
    public void ExpiredSignals_AreNotReturned()
    {
        var store = new SignalStore();
        using var sender = PeerIdentity.Generate();
        using var target = PeerIdentity.Generate();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        Assert.True(store.TryEnqueue(
            sender.PeerId,
            target.PeerId,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "offer",
            "payload",
            now,
            now.AddSeconds(10),
            out _));

        Assert.Empty(store.Fetch(target.PeerId, now.AddSeconds(11)));
    }

    [Fact]
    public void Acknowledgement_CannotRemoveAnotherTargetsSignal()
    {
        var store = new SignalStore();
        using var sender = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        using var charlie = PeerIdentity.Generate();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        Assert.True(store.TryEnqueue(
            sender.PeerId,
            bob.PeerId,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "candidate",
            "payload",
            now,
            now.AddSeconds(30),
            out var signal));

        Assert.Equal(0, store.Acknowledge(charlie.PeerId, new[] { signal.SignalId }, now));
        Assert.Single(store.Fetch(bob.PeerId, now));
    }

    [Fact]
    public void PendingInbox_IsBounded()
    {
        var store = new SignalStore();
        using var sender = PeerIdentity.Generate();
        using var target = PeerIdentity.Generate();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        for (var i = 0; i < SignalStore.MaximumPendingPerPeer; i++)
        {
            Assert.True(store.TryEnqueue(
                sender.PeerId,
                target.PeerId,
                $"{i:x32}",
                "candidate",
                $"candidate-{i}",
                now,
                now.AddSeconds(30),
                out _));
        }

        Assert.False(store.TryEnqueue(
            sender.PeerId,
            target.PeerId,
            "ffffffffffffffffffffffffffffffff",
            "candidate",
            "overflow",
            now,
            now.AddSeconds(30),
            out _));
    }
}
