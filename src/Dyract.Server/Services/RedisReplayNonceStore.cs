using System.Security.Cryptography;
using System.Text;
using Dyract.Core.Identity;
using StackExchange.Redis;

namespace Dyract.Server.Services;

/// <summary>
/// Shared replay protection for signed directory operations. Redis keys are derived from
/// SHA-256(peerId + nonce) so raw Peer IDs/nonces are not exposed in key names.
/// </summary>
public sealed class RedisReplayNonceStore : IReplayNonceStore
{
    private const string DefaultKeyPrefix = "dyract";

    private readonly IDatabase _database;
    private readonly string _keyPrefix;

    public RedisReplayNonceStore(IConnectionMultiplexer connection)
        : this(connection, DefaultKeyPrefix)
    {
    }

    public RedisReplayNonceStore(IConnectionMultiplexer connection, string keyPrefix)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        _database = connection.GetDatabase();
        _keyPrefix = keyPrefix.TrimEnd(':');
    }

    public async ValueTask<bool> TryAcceptAsync(
        PeerId requester,
        string nonce,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        var key = BuildKey(requester, nonce);
        return await _database.StringSetAsync(
            key,
            "1",
            ReplayNonceStore.Lifetime,
            when: When.NotExists);
    }

    private RedisKey BuildKey(PeerId requester, string nonce)
    {
        var material = Encoding.UTF8.GetBytes($"{requester.Value}\n{nonce}");
        var digest = SHA256.HashData(material);
        return $"{_keyPrefix}:replay:{Convert.ToHexStringLower(digest)}";
    }
}
