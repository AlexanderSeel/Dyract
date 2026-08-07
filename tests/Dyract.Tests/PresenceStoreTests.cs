using Dyract.Core.Identity;
using Dyract.Protocol;
using Dyract.Server.Services;
using Xunit;

namespace Dyract.Tests;

public sealed class PresenceStoreTests
{
    [Fact]
    public void PresenceLease_IsAvailableUntilExpiry()
    {
        var store = new PresenceStore();
        var peerId = PeerId.Parse("dyr_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var candidate = new ConnectionCandidate("host", "udp", "192.168.1.20", 45000, 100);

        store.Publish(peerId, new[] { candidate }, now, now.AddSeconds(90));

        Assert.True(store.TryGet(peerId, now.AddSeconds(30), out var lease));
        Assert.Single(lease.Candidates);
        Assert.Equal(candidate, lease.Candidates[0]);
    }

    [Fact]
    public void PresenceLease_IsRemovedAfterExpiry()
    {
        var store = new PresenceStore();
        var peerId = PeerId.Parse("dyr_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        store.Publish(
            peerId,
            new[] { new ConnectionCandidate("host", "udp", "10.0.0.20", 45001, 100) },
            now,
            now.AddSeconds(60));

        Assert.False(store.TryGet(peerId, now.AddSeconds(61), out _));
    }
}
