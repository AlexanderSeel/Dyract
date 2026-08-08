using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dyract.Core.Identity;
using StackExchange.Redis;

namespace Dyract.Server.Services;

/// <summary>
/// Shared short-lived WebRTC signaling inbox. The target PeerId is SHA-256-derived in Redis
/// key names; envelope values contain only the protocol-approved transient signaling metadata.
/// Fetch is non-destructive and items remain until explicit ACK or natural expiry.
/// </summary>
public sealed class RedisSignalStore : ISignalStore
{
    private const string DefaultKeyPrefix = "dyract";

    private const string EnqueueScript = """
        local expired = redis.call('ZRANGEBYSCORE', KEYS[3], '-inf', ARGV[1])
        for _, id in ipairs(expired) do
            redis.call('HDEL', KEYS[1], id)
            redis.call('ZREM', KEYS[2], id)
            redis.call('ZREM', KEYS[3], id)
        end

        if redis.call('HLEN', KEYS[1]) >= tonumber(ARGV[2]) then
            return 0
        end

        redis.call('HSET', KEYS[1], ARGV[3], ARGV[4])
        redis.call('ZADD', KEYS[2], ARGV[5], ARGV[3])
        redis.call('ZADD', KEYS[3], ARGV[6], ARGV[3])

        local maximum = redis.call('ZREVRANGE', KEYS[3], 0, 0, 'WITHSCORES')
        if #maximum >= 2 then
            local ttl = tonumber(maximum[2]) - tonumber(ARGV[1])
            if ttl > 0 then
                redis.call('PEXPIRE', KEYS[1], ttl)
                redis.call('PEXPIRE', KEYS[2], ttl)
                redis.call('PEXPIRE', KEYS[3], ttl)
            end
        end

        return 1
        """;

    private const string FetchScript = """
        local expired = redis.call('ZRANGEBYSCORE', KEYS[3], '-inf', ARGV[1])
        for _, id in ipairs(expired) do
            redis.call('HDEL', KEYS[1], id)
            redis.call('ZREM', KEYS[2], id)
            redis.call('ZREM', KEYS[3], id)
        end

        if redis.call('HLEN', KEYS[1]) == 0 then
            redis.call('DEL', KEYS[1], KEYS[2], KEYS[3])
            return '[]'
        end

        local ids = redis.call('ZRANGE', KEYS[2], 0, tonumber(ARGV[2]) - 1)
        local payloads = {}
        for _, id in ipairs(ids) do
            local payload = redis.call('HGET', KEYS[1], id)
            if payload then
                table.insert(payloads, payload)
            end
        end

        return '[' .. table.concat(payloads, ',') .. ']'
        """;

    private const string AcknowledgeScript = """
        local expired = redis.call('ZRANGEBYSCORE', KEYS[3], '-inf', ARGV[1])
        for _, id in ipairs(expired) do
            redis.call('HDEL', KEYS[1], id)
            redis.call('ZREM', KEYS[2], id)
            redis.call('ZREM', KEYS[3], id)
        end

        local removed = 0
        for index = 2, #ARGV do
            local id = ARGV[index]
            removed = removed + redis.call('HDEL', KEYS[1], id)
            redis.call('ZREM', KEYS[2], id)
            redis.call('ZREM', KEYS[3], id)
        end

        if redis.call('HLEN', KEYS[1]) == 0 then
            redis.call('DEL', KEYS[1], KEYS[2], KEYS[3])
        end

        return removed
        """;

    private readonly IDatabase _database;
    private readonly string _keyPrefix;

    public RedisSignalStore(IConnectionMultiplexer connection)
        : this(connection, DefaultKeyPrefix)
    {
    }

    public RedisSignalStore(IConnectionMultiplexer connection, string keyPrefix)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        _database = connection.GetDatabase();
        _keyPrefix = keyPrefix.TrimEnd(':');
    }

    public async ValueTask<StoredPeerSignal?> TryEnqueueAsync(
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
        if (expiresAt <= createdAt)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "Signal expiry must be after creation time.");
        }

        var signal = new StoredPeerSignal(
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            senderPeerId,
            targetPeerId,
            sessionId,
            signalType,
            payload,
            createdAt,
            expiresAt);
        var serialized = JsonSerializer.Serialize(RedisStoredSignal.From(signal));
        var keys = BuildKeys(targetPeerId);

        var result = await _database.ScriptEvaluateAsync(
            EnqueueScript,
            [keys.Items, keys.Order, keys.Expiry],
            [
                createdAt.ToUnixTimeMilliseconds(),
                SignalStore.MaximumPendingPerPeer,
                signal.SignalId,
                serialized,
                createdAt.ToUnixTimeMilliseconds(),
                expiresAt.ToUnixTimeMilliseconds()
            ]);

        return string.Equals(result.ToString(), "1", StringComparison.Ordinal)
            ? signal
            : null;
    }

    public async ValueTask<IReadOnlyList<StoredPeerSignal>> FetchAsync(
        PeerId targetPeerId,
        DateTimeOffset now,
        int limit = SignalStore.MaximumFetchCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > SignalStore.MaximumFetchCount)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var keys = BuildKeys(targetPeerId);
        var result = await _database.ScriptEvaluateAsync(
            FetchScript,
            [keys.Items, keys.Order, keys.Expiry],
            [now.ToUnixTimeMilliseconds(), limit]);
        var json = result.ToString();

        RedisStoredSignal[]? payloads;
        try
        {
            payloads = JsonSerializer.Deserialize<RedisStoredSignal[]>(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Redis signaling inbox contains malformed Dyract state.", exception);
        }

        if (payloads is null || payloads.Length == 0)
        {
            return Array.Empty<StoredPeerSignal>();
        }

        var signals = new List<StoredPeerSignal>(payloads.Length);
        foreach (var payload in payloads)
        {
            if (!payload.TryToStoredSignal(targetPeerId, out var signal))
            {
                throw new InvalidOperationException("Redis signaling inbox contains invalid Dyract envelope state.");
            }

            signals.Add(signal);
        }

        return signals;
    }

    public async ValueTask<int> AcknowledgeAsync(
        PeerId targetPeerId,
        IReadOnlyCollection<string> signalIds,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(signalIds);
        if (signalIds.Count == 0)
        {
            return 0;
        }

        var keys = BuildKeys(targetPeerId);
        var arguments = new RedisValue[signalIds.Count + 1];
        arguments[0] = now.ToUnixTimeMilliseconds();
        var index = 1;
        foreach (var signalId in signalIds)
        {
            arguments[index++] = signalId;
        }

        var result = await _database.ScriptEvaluateAsync(
            AcknowledgeScript,
            [keys.Items, keys.Order, keys.Expiry],
            arguments);

        return int.Parse(result.ToString(), CultureInfo.InvariantCulture);
    }

    private InboxKeys BuildKeys(PeerId targetPeerId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(targetPeerId.Value));
        var hashTag = Convert.ToHexStringLower(digest);
        var prefix = $"{_keyPrefix}:signal:{{{hashTag}}}";
        return new InboxKeys(
            $"{prefix}:items",
            $"{prefix}:order",
            $"{prefix}:expiry");
    }

    private readonly record struct InboxKeys(RedisKey Items, RedisKey Order, RedisKey Expiry);

    private sealed record RedisStoredSignal(
        string SignalId,
        string SenderPeerId,
        string TargetPeerId,
        string SessionId,
        string SignalType,
        string Payload,
        long CreatedUnixMilliseconds,
        long ExpiresUnixMilliseconds)
    {
        public static RedisStoredSignal From(StoredPeerSignal signal)
            => new(
                signal.SignalId,
                signal.SenderPeerId.Value,
                signal.TargetPeerId.Value,
                signal.SessionId,
                signal.SignalType,
                signal.Payload,
                signal.CreatedAt.ToUnixTimeMilliseconds(),
                signal.ExpiresAt.ToUnixTimeMilliseconds());

        public bool TryToStoredSignal(PeerId expectedTarget, out StoredPeerSignal signal)
        {
            signal = null!;
            if (!PeerId.TryParse(SenderPeerId, out var sender) ||
                !PeerId.TryParse(TargetPeerId, out var target) ||
                target != expectedTarget)
            {
                return false;
            }

            try
            {
                var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(CreatedUnixMilliseconds);
                var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(ExpiresUnixMilliseconds);
                if (expiresAt <= createdAt)
                {
                    return false;
                }

                signal = new StoredPeerSignal(
                    SignalId,
                    sender,
                    target,
                    SessionId,
                    SignalType,
                    Payload,
                    createdAt,
                    expiresAt);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }
    }
}
