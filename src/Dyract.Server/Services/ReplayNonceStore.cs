using System.Collections.Concurrent;
using Dyract.Core.Identity;

namespace Dyract.Server.Services;

public sealed class ReplayNonceStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, long> _nonces = new(StringComparer.Ordinal);

    public bool TryAccept(PeerId requester, string nonce, DateTimeOffset now)
    {
        foreach (var item in _nonces)
        {
            if (item.Value <= now.ToUnixTimeSeconds())
            {
                _nonces.TryRemove(item.Key, out _);
            }
        }

        var key = $"{requester.Value}:{nonce}";
        var expires = now.Add(Lifetime).ToUnixTimeSeconds();
        return _nonces.TryAdd(key, expires);
    }
}
