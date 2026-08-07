using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Dyract.Transport;
using Xunit;

namespace Dyract.Tests;

public sealed class PeerConnectionDescriptorTests
{
    [Fact]
    public void ReachableResponse_CreatesTransportDescriptor()
    {
        using var peer = PeerIdentity.Generate();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var response = new ResolvePeerResponse(
            peer.PeerId.Value,
            Convert.ToBase64String(peer.ExportPublicKey()),
            IsReachable: true,
            Candidates:
            [
                new ConnectionCandidate("host", "udp", "192.168.1.20", 45000, 100),
                new ConnectionCandidate("srflx", "udp", "203.0.113.20", 52000, 90)
            ],
            LeaseExpiresUnixSeconds: now.AddMinutes(1).ToUnixTimeSeconds());

        var descriptor = PeerConnectionDescriptorFactory.Create(response, now);

        Assert.Equal(peer.PeerId, descriptor.PeerId);
        Assert.Equal(2, descriptor.Candidates.Count);
        Assert.False(descriptor.HasRelayCandidate);
    }

    [Fact]
    public void DirectOnlyMode_RemovesRelayCandidates()
    {
        using var peer = PeerIdentity.Generate();
        var descriptor = new PeerConnectionDescriptor(
            peer.PeerId,
            [
                new ConnectionCandidate("srflx", "udp", "203.0.113.20", 52000, 100),
                new ConnectionCandidate("relay", "udp", "198.51.100.10", 53000, 90)
            ],
            DateTimeOffset.UtcNow.AddMinutes(1));

        var selected = PeerConnectionDescriptorFactory.SelectCandidates(
            descriptor,
            PeerTransportMode.DirectOnly);

        Assert.Single(selected);
        Assert.Equal("srflx", selected[0].Kind);
    }

    [Fact]
    public void DirectOnlyMode_FailsWhenOnlyRelayCandidateExists()
    {
        using var peer = PeerIdentity.Generate();
        var descriptor = new PeerConnectionDescriptor(
            peer.PeerId,
            [new ConnectionCandidate("relay", "udp", "198.51.100.10", 53000, 90)],
            DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() =>
            PeerConnectionDescriptorFactory.SelectCandidates(descriptor, PeerTransportMode.DirectOnly));
    }

    [Fact]
    public void ExpiredLease_IsRejectedBeforeTransportAttempt()
    {
        using var peer = PeerIdentity.Generate();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var response = new ResolvePeerResponse(
            peer.PeerId.Value,
            Convert.ToBase64String(peer.ExportPublicKey()),
            IsReachable: true,
            Candidates: [new ConnectionCandidate("host", "udp", "10.0.0.20", 45000, 100)],
            LeaseExpiresUnixSeconds: now.AddSeconds(-1).ToUnixTimeSeconds());

        Assert.Throws<InvalidOperationException>(() =>
            PeerConnectionDescriptorFactory.Create(response, now));
    }

    [Fact]
    public void UnsafeCandidateAddress_IsRejectedClientSide()
    {
        using var peer = PeerIdentity.Generate();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var response = new ResolvePeerResponse(
            peer.PeerId.Value,
            Convert.ToBase64String(peer.ExportPublicKey()),
            IsReachable: true,
            Candidates: [new ConnectionCandidate("host", "udp", "127.0.0.1", 45000, 100)],
            LeaseExpiresUnixSeconds: now.AddMinutes(1).ToUnixTimeSeconds());

        Assert.Throws<ArgumentException>(() =>
            PeerConnectionDescriptorFactory.Create(response, now));
    }
}
