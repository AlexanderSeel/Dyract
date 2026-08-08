using System.Security.Cryptography;
using System.Text.Json;
using Dyract.Core.Identity;
using StackExchange.Redis;

namespace Dyract.Server.Services;

/// <summary>
/// Shared two-minute registration challenge state. The Redis key contains only the random
/// challenge ID; peer/key material is held inside the TTL-bound value and removed on consume.
/// </summary>
public sealed class RedisRegistrationChallengeStore : IRegistrationChallengeStore
{
    private const string DefaultKeyPrefix = "dyract";

    private readonly IDatabase _database;
    private readonly string _keyPrefix;

    public RedisRegistrationChallengeStore(IConnectionMultiplexer connection)
        : this(connection, DefaultKeyPrefix)
    {
    }

    public RedisRegistrationChallengeStore(IConnectionMultiplexer connection, string keyPrefix)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        _database = connection.GetDatabase();
        _keyPrefix = keyPrefix.TrimEnd(':');
    }

    public async ValueTask<RegistrationChallenge> CreateAsync(
        PeerId peerId,
        byte[] publicKey,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(publicKey);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var challenge = new RegistrationChallenge(
                Guid.NewGuid().ToString("N"),
                peerId,
                publicKey.ToArray(),
                RandomNumberGenerator.GetBytes(32),
                now.Add(RegistrationChallengeStore.Lifetime));
            var payload = new RedisChallengePayload(
                peerId.Value,
                Convert.ToBase64String(challenge.PublicKey),
                Convert.ToBase64String(challenge.ChallengeBytes),
                challenge.ExpiresAt.ToUnixTimeMilliseconds());

            if (await _database.StringSetAsync(
                    BuildKey(challenge.Id),
                    JsonSerializer.Serialize(payload),
                    RegistrationChallengeStore.Lifetime,
                    when: When.NotExists))
            {
                return challenge;
            }
        }
    }

    public async ValueTask<RegistrationChallenge?> GetAsync(
        string challengeId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidChallengeId(challengeId))
        {
            return null;
        }

        var key = BuildKey(challengeId);
        var value = await _database.StringGetAsync(key);
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        RedisChallengePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<RedisChallengePayload>((string)value!);
        }
        catch (JsonException)
        {
            await _database.KeyDeleteAsync(key);
            return null;
        }

        if (payload is null || !PeerId.TryParse(payload.PeerId, out var peerId))
        {
            await _database.KeyDeleteAsync(key);
            return null;
        }

        try
        {
            var publicKey = Convert.FromBase64String(payload.PublicKeyBase64);
            var challengeBytes = Convert.FromBase64String(payload.ChallengeBase64);
            var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(payload.ExpiresUnixMilliseconds);
            if (expiresAt <= now || challengeBytes.Length != 32 || publicKey.Length == 0)
            {
                await _database.KeyDeleteAsync(key);
                return null;
            }

            return new RegistrationChallenge(
                challengeId,
                peerId,
                publicKey,
                challengeBytes,
                expiresAt);
        }
        catch (FormatException)
        {
            await _database.KeyDeleteAsync(key);
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            await _database.KeyDeleteAsync(key);
            return null;
        }
    }

    public async ValueTask<bool> TryConsumeAsync(
        string challengeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return IsValidChallengeId(challengeId) &&
               await _database.KeyDeleteAsync(BuildKey(challengeId));
    }

    private RedisKey BuildKey(string challengeId)
        => $"{_keyPrefix}:registration:{challengeId}";

    private static bool IsValidChallengeId(string? challengeId)
        => challengeId is { Length: 32 } && challengeId.All(Uri.IsHexDigit);

    private sealed record RedisChallengePayload(
        string PeerId,
        string PublicKeyBase64,
        string ChallengeBase64,
        long ExpiresUnixMilliseconds);
}
