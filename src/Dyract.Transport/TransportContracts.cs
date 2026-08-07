using Dyract.Core.Identity;
using Dyract.Protocol;

namespace Dyract.Transport;

public enum PeerTransportMode
{
    DirectOnly = 0,
    AllowRelay = 1
}

public enum PeerConnectionState
{
    New = 0,
    Connecting = 1,
    Connected = 2,
    Disconnected = 3,
    Failed = 4,
    Closed = 5
}

public sealed record TurnServerDefinition(
    Uri Uri,
    string? Username = null,
    string? Credential = null);

public sealed record PeerTransportOptions(
    IReadOnlyList<Uri> StunServers,
    IReadOnlyList<TurnServerDefinition> TurnServers,
    PeerTransportMode Mode)
{
    public static PeerTransportOptions DirectOnly(params Uri[] stunServers)
        => new(stunServers, Array.Empty<TurnServerDefinition>(), PeerTransportMode.DirectOnly);
}

public sealed record PeerConnectionDescriptor(
    PeerId PeerId,
    IReadOnlyList<ConnectionCandidate> Candidates,
    DateTimeOffset LeaseExpiresAt)
{
    public bool HasRelayCandidate => Candidates.Any(candidate => candidate.Kind == "relay");
}

public sealed record PeerFrame(byte[] Payload)
{
    public ReadOnlyMemory<byte> Data => Payload;
}

public interface IPeerConnection : IAsyncDisposable
{
    PeerId PeerId { get; }
    PeerConnectionState State { get; }

    ValueTask SendAsync(
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<PeerFrame> ReceiveAsync(
        CancellationToken cancellationToken = default);
}

public interface IPeerTransport : IAsyncDisposable
{
    PeerTransportOptions Options { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task<IPeerConnection> ConnectAsync(
        PeerConnectionDescriptor peer,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<IPeerConnection> AcceptAsync(
        CancellationToken cancellationToken = default);
}
