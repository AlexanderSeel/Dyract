using System.Collections.Concurrent;
using System.Security.Cryptography;
using Dyract.Core.Identity;

namespace Dyract.Server.Services;

public sealed class IdentityStore
{
    private readonly ConcurrentDictionary<string, RegisteredPeer> _peers = new(StringComparer.Ordinal);

    public bool TryRegister(PeerId peerId, byte[] publicKey, DateTimeOffset registeredAt, out RegisteredPeer peer)
    {
        ArgumentNullException.ThrowIfNull(publicKey);

        var candidate = new RegisteredPeer(peerId, publicKey.ToArray(), registeredAt);

        if (_peers.TryAdd(peerId.Value, candidate))
        {
            peer = candidate;
            return true;
        }

        peer = _peers[peerId.Value];
        return CryptographicOperations.FixedTimeEquals(peer.PublicKey, publicKey);
    }

    public bool TryGet(PeerId peerId, out RegisteredPeer peer)
        => _peers.TryGetValue(peerId.Value, out peer!);
}

public sealed record RegisteredPeer(
    PeerId PeerId,
    byte[] PublicKey,
    DateTimeOffset RegisteredAt);
