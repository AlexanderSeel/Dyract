using System.Security.Cryptography;
using Dyract.Core.Identity;

namespace Dyract.Server.Services;

public interface ISignalStore
{
    ValueTask<StoredPeerSignal?> TryEnqueueAsync(
        PeerId senderPeerId,
        PeerId targetPeerId,
        string sessionId,
        string signalType,
        string payload,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<StoredPeerSignal>> FetchAsync(
        PeerId targetPeerId,
        DateTimeOffset now,
        int limit = SignalStore.MaximumFetchCount,
        CancellationToken cancellationToken = default);

    ValueTask<int> AcknowledgeAsync(
        PeerId targetPeerId,
        IReadOnlyCollection<string> signalIds,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

public sealed class SignalStore : ISignalStore
{
    public const int MaximumPendingPerPeer = 64;
    public const int MaximumFetchCount = 20;

    private readonly object _gate = new();
    private readonly Dictionary<string, List<StoredPeerSignal>> _signals = new(StringComparer.Ordinal);

    public ValueTask<StoredPeerSignal?> TryEnqueueAsync(
        PeerId senderPeerId,
        PeerId targetPeerId,
        string sessionId,
        string signalType,
        string payload,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            PurgeExpired(createdAt);

            if (!_signals.TryGetValue(targetPeerId.Value, out var inbox))
            {
                inbox = new List<StoredPeerSignal>();
                _signals[targetPeerId.Value] = inbox;
            }

            if (inbox.Count >= MaximumPendingPerPeer)
            {
                return ValueTask.FromResult<StoredPeerSignal?>(null);
            }

            var signal = new StoredPeerSignal(
                Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                senderPeerId,
                targetPeerId,
                sessionId,
                signalType,
                payload,
                createdAt,
                expiresAt);
            inbox.Add(signal);
            return ValueTask.FromResult<StoredPeerSignal?>(signal);
        }
    }

    public ValueTask<IReadOnlyList<StoredPeerSignal>> FetchAsync(
        PeerId targetPeerId,
        DateTimeOffset now,
        int limit = MaximumFetchCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > MaximumFetchCount)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        lock (_gate)
        {
            PurgeExpired(now);

            if (!_signals.TryGetValue(targetPeerId.Value, out var inbox) || inbox.Count == 0)
            {
                return ValueTask.FromResult<IReadOnlyList<StoredPeerSignal>>(Array.Empty<StoredPeerSignal>());
            }

            IReadOnlyList<StoredPeerSignal> result = inbox
                .OrderBy(signal => signal.CreatedAt)
                .ThenBy(signal => signal.SignalId, StringComparer.Ordinal)
                .Take(limit)
                .ToArray();
            return ValueTask.FromResult(result);
        }
    }

    public ValueTask<int> AcknowledgeAsync(
        PeerId targetPeerId,
        IReadOnlyCollection<string> signalIds,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(signalIds);
        if (signalIds.Count == 0)
        {
            return ValueTask.FromResult(0);
        }

        var ids = new HashSet<string>(signalIds, StringComparer.Ordinal);
        lock (_gate)
        {
            PurgeExpired(now);

            if (!_signals.TryGetValue(targetPeerId.Value, out var inbox))
            {
                return ValueTask.FromResult(0);
            }

            var removed = inbox.RemoveAll(signal => ids.Contains(signal.SignalId));
            if (inbox.Count == 0)
            {
                _signals.Remove(targetPeerId.Value);
            }

            return ValueTask.FromResult(removed);
        }
    }

    private void PurgeExpired(DateTimeOffset now)
    {
        foreach (var peerId in _signals.Keys.ToArray())
        {
            var inbox = _signals[peerId];
            inbox.RemoveAll(signal => signal.ExpiresAt <= now);
            if (inbox.Count == 0)
            {
                _signals.Remove(peerId);
            }
        }
    }
}

public sealed record StoredPeerSignal(
    string SignalId,
    PeerId SenderPeerId,
    PeerId TargetPeerId,
    string SessionId,
    string SignalType,
    string Payload,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);
