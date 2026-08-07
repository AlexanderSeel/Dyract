using System.Security.Cryptography;
using Dyract.Core.Identity;
using Dyract.Protocol;

namespace Dyract.Server.Services;

public sealed class SignalStore
{
    public const int MaximumPendingPerPeer = 64;
    public const int MaximumFetchCount = 20;

    private readonly object _gate = new();
    private readonly Dictionary<string, List<StoredPeerSignal>> _signals = new(StringComparer.Ordinal);

    public bool TryEnqueue(
        PeerId senderPeerId,
        PeerId targetPeerId,
        string sessionId,
        string signalType,
        string payload,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        out StoredPeerSignal signal)
    {
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
                signal = default!;
                return false;
            }

            signal = new StoredPeerSignal(
                Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                senderPeerId,
                targetPeerId,
                sessionId,
                signalType,
                payload,
                createdAt,
                expiresAt);
            inbox.Add(signal);
            return true;
        }
    }

    public IReadOnlyList<StoredPeerSignal> Fetch(
        PeerId targetPeerId,
        DateTimeOffset now,
        int limit = MaximumFetchCount)
    {
        if (limit is < 1 or > MaximumFetchCount)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        lock (_gate)
        {
            PurgeExpired(now);

            if (!_signals.TryGetValue(targetPeerId.Value, out var inbox) || inbox.Count == 0)
            {
                return Array.Empty<StoredPeerSignal>();
            }

            return inbox
                .OrderBy(signal => signal.CreatedAt)
                .ThenBy(signal => signal.SignalId, StringComparer.Ordinal)
                .Take(limit)
                .ToArray();
        }
    }

    public int Acknowledge(
        PeerId targetPeerId,
        IReadOnlyCollection<string> signalIds,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(signalIds);
        if (signalIds.Count == 0)
        {
            return 0;
        }

        var ids = new HashSet<string>(signalIds, StringComparer.Ordinal);
        lock (_gate)
        {
            PurgeExpired(now);

            if (!_signals.TryGetValue(targetPeerId.Value, out var inbox))
            {
                return 0;
            }

            var removed = inbox.RemoveAll(signal => ids.Contains(signal.SignalId));
            if (inbox.Count == 0)
            {
                _signals.Remove(targetPeerId.Value);
            }

            return removed;
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
