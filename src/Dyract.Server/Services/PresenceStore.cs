using System.Collections.Concurrent;
using Dyract.Core.Identity;
using Dyract.Protocol;

namespace Dyract.Server.Services;

public sealed record PresenceLease(
    PeerId PeerId,
    ConnectionCandidate[] Candidates,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt);

public sealed class PresenceStore
{
    private readonly ConcurrentDictionary<string, PresenceLease> _leases = new(StringComparer.Ordinal);

    public PresenceLease Publish(
        PeerId peerId,
        IReadOnlyList<ConnectionCandidate> candidates,
        DateTimeOffset now,
        DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var lease = new PresenceLease(
            peerId,
            candidates.ToArray(),
            now,
            expiresAt);

        _leases[peerId.Value] = lease;
        RemoveExpired(now);
        return lease;
    }

    public bool TryGet(PeerId peerId, DateTimeOffset now, out PresenceLease lease)
    {
        RemoveExpired(now);

        if (_leases.TryGetValue(peerId.Value, out var found) && found.ExpiresAt > now)
        {
            lease = found with { Candidates = found.Candidates.ToArray() };
            return true;
        }

        lease = null!;
        return false;
    }

    public bool Remove(PeerId peerId)
        => _leases.TryRemove(peerId.Value, out _);

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var item in _leases)
        {
            if (item.Value.ExpiresAt <= now)
            {
                _leases.TryRemove(item.Key, out _);
            }
        }
    }
}
