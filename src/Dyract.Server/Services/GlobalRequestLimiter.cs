using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace Dyract.Server.Services;

public enum DirectoryRateLimitCategory
{
    Registration = 0,
    PeerOperations = 1
}

public sealed record GlobalRateLimitDecision(
    bool IsAllowed,
    TimeSpan RetryAfter);

public interface IGlobalRequestLimiter
{
    ValueTask<GlobalRateLimitDecision> AcquireAsync(
        DirectoryRateLimitCategory category,
        string clientPartitionKey,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Zero-setup/single-process mode. The existing ASP.NET Core limiter remains active locally;
/// this implementation adds no second distributed admission layer when Redis is absent.
/// </summary>
public sealed class NoOpGlobalRequestLimiter : IGlobalRequestLimiter
{
    public ValueTask<GlobalRateLimitDecision> AcquireAsync(
        DirectoryRateLimitCategory category,
        string clientPartitionKey,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new GlobalRateLimitDecision(true, TimeSpan.Zero));
    }
}

/// <summary>
/// Redis-backed fixed-window admission shared by every directory instance. Client partition
/// identifiers are SHA-256-derived before entering Redis key names.
/// </summary>
public sealed class RedisGlobalRequestLimiter : IGlobalRequestLimiter
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    public const int RegistrationPermitLimit = 30;
    public const int PeerOperationsPermitLimit = 240;

    private const string DefaultKeyPrefix = "dyract";
    private const string IncrementScript = """
        local count = redis.call('INCR', KEYS[1])
        if count == 1 then
            redis.call('PEXPIRE', KEYS[1], ARGV[1])
        end
        return count
        """;

    private readonly IDatabase _database;
    private readonly string _keyPrefix;

    public RedisGlobalRequestLimiter(IConnectionMultiplexer connection)
        : this(connection, DefaultKeyPrefix)
    {
    }

    public RedisGlobalRequestLimiter(IConnectionMultiplexer connection, string keyPrefix)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        _database = connection.GetDatabase();
        _keyPrefix = keyPrefix.TrimEnd(':');
    }

    public async ValueTask<GlobalRateLimitDecision> AcquireAsync(
        DirectoryRateLimitCategory category,
        string clientPartitionKey,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(clientPartitionKey);

        var windowSeconds = checked((long)Window.TotalSeconds);
        var unixSeconds = now.ToUnixTimeSeconds();
        var bucket = Math.DivRem(unixSeconds, windowSeconds, out var offset);
        if (offset < 0)
        {
            bucket--;
            offset += windowSeconds;
        }

        var retryAfterSeconds = windowSeconds - offset;
        var permitLimit = category switch
        {
            DirectoryRateLimitCategory.Registration => RegistrationPermitLimit,
            DirectoryRateLimitCategory.PeerOperations => PeerOperationsPermitLimit,
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        };

        var categoryName = category switch
        {
            DirectoryRateLimitCategory.Registration => "registration",
            DirectoryRateLimitCategory.PeerOperations => "peer",
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        };

        var partitionDigest = SHA256.HashData(Encoding.UTF8.GetBytes(clientPartitionKey));
        var partitionToken = Convert.ToHexStringLower(partitionDigest);
        RedisKey key = $"{_keyPrefix}:ratelimit:{categoryName}:{partitionToken}:{bucket}";

        // Keep the bucket slightly longer than its logical window so clock skew/retries cannot
        // resurrect it immediately after expiry, while still making the state short-lived.
        var redisTtlMilliseconds = checked((long)(Window + TimeSpan.FromMinutes(1)).TotalMilliseconds);
        var result = await _database.ScriptEvaluateAsync(
            IncrementScript,
            [key],
            [redisTtlMilliseconds]);
        var count = (long)result;

        return count <= permitLimit
            ? new GlobalRateLimitDecision(true, TimeSpan.Zero)
            : new GlobalRateLimitDecision(false, TimeSpan.FromSeconds(Math.Max(1, retryAfterSeconds)));
    }
}
