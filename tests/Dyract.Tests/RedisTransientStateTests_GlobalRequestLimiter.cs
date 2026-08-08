using Dyract.Server.Services;
using StackExchange.Redis;
using Xunit;

namespace Dyract.Tests;

public sealed class RedisTransientStateTests_GlobalRequestLimiter
{
    private const string ConnectionEnvironmentVariable = "DYRACT_REDIS_TEST_CONNECTION";

    [Fact]
    public async Task RegistrationLimit_IsSharedAcrossInstancesAndResetsAtNextWindow()
    {
        var connectionString = GetConnectionStringOrSkip();
        if (connectionString is null)
        {
            return;
        }

        using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var prefix = $"dyract:test:{Guid.NewGuid():N}";
        var first = new RedisGlobalRequestLimiter(connection, prefix);
        var second = new RedisGlobalRequestLimiter(connection, prefix);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_010);
        const string partition = "198.51.100.10";

        for (var index = 0; index < RedisGlobalRequestLimiter.RegistrationPermitLimit; index++)
        {
            var limiter = index % 2 == 0 ? first : second;
            var decision = await limiter.AcquireAsync(
                DirectoryRateLimitCategory.Registration,
                partition,
                now);
            Assert.True(decision.IsAllowed);
        }

        var rejected = await second.AcquireAsync(
            DirectoryRateLimitCategory.Registration,
            partition,
            now);
        Assert.False(rejected.IsAllowed);
        Assert.InRange(rejected.RetryAfter, TimeSpan.FromSeconds(1), RedisGlobalRequestLimiter.Window);

        var nextWindow = await first.AcquireAsync(
            DirectoryRateLimitCategory.Registration,
            partition,
            now.Add(RedisGlobalRequestLimiter.Window));
        Assert.True(nextWindow.IsAllowed);
    }

    [Fact]
    public async Task ClientPartitionsAndCategoriesAreIndependent()
    {
        var connectionString = GetConnectionStringOrSkip();
        if (connectionString is null)
        {
            return;
        }

        using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var prefix = $"dyract:test:{Guid.NewGuid():N}";
        var limiter = new RedisGlobalRequestLimiter(connection, prefix);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_010);
        const string alice = "203.0.113.10";
        const string bob = "203.0.113.11";

        for (var index = 0; index < RedisGlobalRequestLimiter.RegistrationPermitLimit; index++)
        {
            Assert.True((await limiter.AcquireAsync(
                DirectoryRateLimitCategory.Registration,
                alice,
                now)).IsAllowed);
        }

        Assert.False((await limiter.AcquireAsync(
            DirectoryRateLimitCategory.Registration,
            alice,
            now)).IsAllowed);

        Assert.True((await limiter.AcquireAsync(
            DirectoryRateLimitCategory.Registration,
            bob,
            now)).IsAllowed);

        Assert.True((await limiter.AcquireAsync(
            DirectoryRateLimitCategory.PeerOperations,
            alice,
            now)).IsAllowed);
    }

    [Fact]
    public async Task RedisRateLimitKeysDoNotExposeRawClientPartition()
    {
        var connectionString = GetConnectionStringOrSkip();
        if (connectionString is null)
        {
            return;
        }

        using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var prefix = $"dyract:test:{Guid.NewGuid():N}";
        var limiter = new RedisGlobalRequestLimiter(connection, prefix);
        const string partition = "192.0.2.55";
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_010);

        Assert.True((await limiter.AcquireAsync(
            DirectoryRateLimitCategory.PeerOperations,
            partition,
            now)).IsAllowed);

        var endpoint = connection.GetEndPoints().Single();
        var server = connection.GetServer(endpoint);
        var keys = server.Keys(pattern: $"{prefix}:ratelimit:*").Select(key => key.ToString()).ToArray();

        Assert.NotEmpty(keys);
        Assert.DoesNotContain(keys, key => key.Contains(partition, StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoOpLimiter_AllowsZeroSetupMode()
    {
        var limiter = new NoOpGlobalRequestLimiter();
        var decision = await limiter.AcquireAsync(
            DirectoryRateLimitCategory.Registration,
            "test-client",
            DateTimeOffset.UtcNow);

        Assert.True(decision.IsAllowed);
        Assert.Equal(TimeSpan.Zero, decision.RetryAfter);
    }

    private static string? GetConnectionStringOrSkip()
        => Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
}
