using Dyract.Server.Services;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Xunit;

namespace Dyract.Tests;

public sealed class RedisConnectionPolicyTests
{
    private static readonly object EnvironmentGate = new();

    [Fact]
    public void Development_AllowsLocalUnauthenticatedRedisForTestMode()
    {
        var options = ConfigurationOptions.Parse("localhost:6379,abortConnect=true");

        RedisConnectionPolicy.Validate(options, Environments.Development);
    }

    [Fact]
    public void Production_RejectsRedisWithoutTls()
    {
        var options = ConfigurationOptions.Parse("redis.example.test:6379,password=secret");

        var exception = Assert.Throws<InvalidOperationException>(
            () => RedisConnectionPolicy.Validate(options, Environments.Production, networkIsolationConfirmed: true));

        Assert.Contains("TLS", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_RejectsRedisWithoutAuthenticationSecret()
    {
        var options = ConfigurationOptions.Parse("redis.example.test:6380,ssl=true");

        var exception = Assert.Throws<InvalidOperationException>(
            () => RedisConnectionPolicy.Validate(options, Environments.Production, networkIsolationConfirmed: true));

        Assert.Contains("authentication", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_RejectsAdministrativeRedisConnection()
    {
        var options = ConfigurationOptions.Parse(
            "redis.example.test:6380,ssl=true,password=secret,allowAdmin=true");

        var exception = Assert.Throws<InvalidOperationException>(
            () => RedisConnectionPolicy.Validate(options, Environments.Production, networkIsolationConfirmed: true));

        Assert.Contains("administrative", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_RejectsMissingNetworkIsolationConfirmation()
    {
        var options = ConfigurationOptions.Parse(
            "redis.example.test:6380,ssl=true,password=secret,allowAdmin=false");

        var exception = Assert.Throws<InvalidOperationException>(
            () => RedisConnectionPolicy.Validate(options, Environments.Production, networkIsolationConfirmed: false));

        Assert.Contains("network isolation", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(RedisConnectionPolicy.NetworkIsolationConfirmationKey, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_AcceptsTlsAuthenticatedNonAdminIsolatedConnection()
    {
        var options = ConfigurationOptions.Parse(
            "redis.example.test:6380,ssl=true,password=secret,allowAdmin=false");

        RedisConnectionPolicy.Validate(options, Environments.Production, networkIsolationConfirmed: true);
    }

    [Fact]
    public void Production_DefaultValidationReadsDeploymentEnvironmentConfirmation()
    {
        var options = ConfigurationOptions.Parse(
            "redis.example.test:6380,ssl=true,password=secret,allowAdmin=false");

        lock (EnvironmentGate)
        {
            var previous = Environment.GetEnvironmentVariable(
                RedisConnectionPolicy.NetworkIsolationConfirmationEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(
                    RedisConnectionPolicy.NetworkIsolationConfirmationEnvironmentVariable,
                    bool.TrueString);

                RedisConnectionPolicy.Validate(options, Environments.Production);
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    RedisConnectionPolicy.NetworkIsolationConfirmationEnvironmentVariable,
                    previous);
            }
        }
    }
}
