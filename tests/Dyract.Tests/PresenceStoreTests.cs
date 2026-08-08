using Dyract.Core.Identity;
using Dyract.Protocol;
using Dyract.Server.Services;
using Xunit;

namespace Dyract.Tests;

public sealed class PresenceStoreTests
{
    [Fact]
    public async Task PresenceLease_IsAvailableUntilExpiry()
    {
        var store = new PresenceStore();
        var peerId = PeerId.FromPublicKey(new byte[] { 1, 2, 3 });
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var candidate = new ConnectionCandidate("host", "udp", "192.168.1.20", 45000, 100);

        await store.PublishAsync(peerId, new[] { candidate }, now, now.AddSeconds(90));

        var lease = await store.GetAsync(peerId, now.AddSeconds(30));
        Assert.NotNull(lease);
        Assert.Single(lease.Candidates);
        Assert.Equal(candidate, lease.Candidates[0]);
    }

    [Fact]
    public async Task PresenceLease_IsRemovedAfterExpiry()
    {
        var store = new PresenceStore();
        var peerId = PeerId.FromPublicKey(new byte[] { 4, 5, 6 });
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        await store.PublishAsync(
            peerId,
            new[] { new ConnectionCandidate("host", "udp", "10.0.0.20", 45001, 100) },
            now,
            now.AddSeconds(60));

        Assert.Null(await store.GetAsync(peerId, now.AddSeconds(61)));
    }
}
