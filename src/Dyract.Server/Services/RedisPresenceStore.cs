using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dyract.Core.Identity;
using Dyract.Protocol;
using StackExchange.Redis;

namespace Dyract.Server.Services;

/// <summary>
/// Shared short-lived presence state for multi-instance directory deployments.
/// Values contain only the reachability metadata already approved for the active lease.
/// Redis key names use a SHA-256-derived peer token rather than exposing raw Peer IDs.
/// </summary>
public sealed class RedisPresenceStore : IPresenceStore
{
    private const string DefaultKeyPrefix = "dyract";

    private readonly IDatabase _database;
    private readonly string _keyPrefix;

    public RedisPresenceStore(IConnectionMultiplexer connection)
        : this(connection, DefaultKeyPrefix)
    {
    }

    public RedisPresenceStore(IConnectionMultiplexer connection, string keyPrefix)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        _database = connection.GetDatabase();
        _keyPrefix = keyPrefix.TrimEnd(':');
    }

    public async ValueTask<PresenceLease> PublishAsync(
        PeerId peerId,
        IReadOnlyList<ConnectionCandidate> candidates,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(candidates);

        var ttl = expiresAt - now;
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "Presence expiry must be in the future.");
        }

        var lease = new PresenceLease(peerId, candidates.ToArray(), now, expiresAt);
        var payload = new RedisPresencePayload(
            peerId.Value,
            lease.Candidates,
            now.ToUnixTimeSeconds(),
            expiresAt.ToUnixTimeSeconds());

        var serialized = JsonSerializer.Serialize(payload);
        await _database.StringSetAsync(
            BuildKey(peerId),
            serialized,
            ttl,
            when: When.Always);

        return lease;
    }

    public async ValueTask<PresenceLease?> GetAsync(
        PeerId peerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = BuildKey(peerId);
        var value = await _database.StringGetAsync(key);
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        RedisPresencePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<RedisPresencePayload>((string)value!);
        }
        catch (JsonException)
        {
            await _database.KeyDeleteAsync(key);
            return null;
        }

        if (payload is null ||
            !PeerId.TryParse(payload.PeerId, out var storedPeerId) ||
            storedPeerId != peerId ||
            payload.Candidates is null)
        {
            await _database.KeyDeleteAsync(key);
            return null;
        }

        DateTimeOffset updatedAt;
        DateTimeOffset expiresAt;
        try
        {
            updatedAt = DateTimeOffset.FromUnixTimeSeconds(payload.UpdatedUnixSeconds);
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresUnixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            await _database.KeyDeleteAsync(key);
            return null;
        }

        if (expiresAt <= now)
        {
            await _database.KeyDeleteAsync(key);
            return null;
        }

        return new PresenceLease(
            storedPeerId,
            payload.Candidates.ToArray(),
            updatedAt,
            expiresAt);
    }

    public async ValueTask<bool> RemoveAsync(
        PeerId peerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _database.KeyDeleteAsync(BuildKey(peerId));
    }

    private RedisKey BuildKey(PeerId peerId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(peerId.Value));
        return $"{_keyPrefix}:presence:{Convert.ToHexStringLower(digest)}";
    }

    private sealed record RedisPresencePayload(
        string PeerId,
        ConnectionCandidate[] Candidates,
        long UpdatedUnixSeconds,
        long ExpiresUnixSeconds);
}
