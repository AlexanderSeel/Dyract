using System.Collections.Concurrent;
using Dyract.Core.Identity;
using Dyract.Protocol;

namespace Dyract.Server.Services;

public sealed record PresenceLease(
    PeerId PeerId,
    ConnectionCandidate[] Candidates,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt);

public interface IPresenceStore
{
    ValueTask<PresenceLease> PublishAsync(
        PeerId peerId,
        IReadOnlyList<ConnectionCandidate> candidates,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    ValueTask<PresenceLease?> GetAsync(
        PeerId peerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    ValueTask<bool> RemoveAsync(
        PeerId peerId,
        CancellationToken cancellationToken = default);
}

public sealed class PresenceStore : IPresenceStore
{
    private readonly ConcurrentDictionary<string, PresenceLease> _leases = new(StringComparer.Ordinal);

    public ValueTask<PresenceLease> PublishAsync(
        PeerId peerId,
        IReadOnlyList<ConnectionCandidate> candidates,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(candidates);

        var lease = new PresenceLease(
            peerId,
            candidates.ToArray(),
            now,
            expiresAt);

        _leases[peerId.Value] = lease;
        RemoveExpired(now);
        return ValueTask.FromResult(lease);
    }

    public ValueTask<PresenceLease?> GetAsync(
        PeerId peerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RemoveExpired(now);

        if (_leases.TryGetValue(peerId.Value, out var found) && found.ExpiresAt > now)
        {
            return ValueTask.FromResult<PresenceLease?>(
                found with { Candidates = found.Candidates.ToArray() });
        }

        return ValueTask.FromResult<PresenceLease?>(null);
    }

    public ValueTask<bool> RemoveAsync(
        PeerId peerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_leases.TryRemove(peerId.Value, out _));
    }

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
