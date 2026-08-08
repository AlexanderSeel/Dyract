using System.Collections.Concurrent;
using Dyract.Core.Identity;

namespace Dyract.Server.Services;

public interface IReplayNonceStore
{
    ValueTask<bool> TryAcceptAsync(
        PeerId requester,
        string nonce,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

public sealed class ReplayNonceStore : IReplayNonceStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, long> _nonces = new(StringComparer.Ordinal);

    public ValueTask<bool> TryAcceptAsync(
        PeerId requester,
        string nonce,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var item in _nonces)
        {
            if (item.Value <= now.ToUnixTimeSeconds())
            {
                _nonces.TryRemove(item.Key, out _);
            }
        }

        var key = $"{requester.Value}:{nonce}";
        var expires = now.Add(Lifetime).ToUnixTimeSeconds();
        return ValueTask.FromResult(_nonces.TryAdd(key, expires));
    }
}
