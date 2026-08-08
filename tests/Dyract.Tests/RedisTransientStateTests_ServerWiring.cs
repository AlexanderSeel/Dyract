using Dyract.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dyract.Tests;

public sealed class RedisTransientStateTests_ServerWiring
{
    private const string ConnectionEnvironmentVariable = "DYRACT_REDIS_TEST_CONNECTION";

    [Fact]
    public void ZeroSetupServer_UsesNoOpGlobalLimiter()
    {
        using var factory = new WebApplicationFactory<Program>();
        var limiter = factory.Services.GetRequiredService<IGlobalRequestLimiter>();
        Assert.IsType<NoOpGlobalRequestLimiter>(limiter);
    }

    [Fact]
    public void RedisConfiguredServer_UsesSharedGlobalLimiter()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Minimal hosting reads configuration-dependent registrations while Program is
                // executing. Use a host setting so the value is present before that service
                // selection rather than a late ConfigureAppConfiguration callback.
                builder.UseSetting("ConnectionStrings:Redis", connectionString);
            });

        var limiter = factory.Services.GetRequiredService<IGlobalRequestLimiter>();
        Assert.IsType<RedisGlobalRequestLimiter>(limiter);
    }
}
