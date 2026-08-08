using System.Net;
using System.Net.Http.Json;
using Dyract.Protocol;
using Dyract.Server.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dyract.Tests;

public sealed class GlobalRequestRateLimitMiddlewareTests
{
    [Fact]
    public async Task GlobalRejection_BlocksApiButNotHealthAndReturnsRetryAfter()
    {
        var limiter = new RejectingLimiter(TimeSpan.FromSeconds(17));
        using var factory = CreateFactory(limiter);
        using var client = factory.CreateClient();

        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Empty(limiter.Categories);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/peer/lookup",
            new PeerLookupRequest("invalid", "invalid", 0, string.Empty, string.Empty));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("17", response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString("0") ??
                           response.Headers.TryGetValues("Retry-After", out var values)
                               ? values.Single()
                               : null);
        Assert.Contains("rate_limited", body, StringComparison.Ordinal);
        Assert.Equal([DirectoryRateLimitCategory.PeerOperations], limiter.Categories);
    }

    [Fact]
    public async Task RegistrationAndPeerEndpoints_UseDistinctGlobalCategories()
    {
        var limiter = new CapturingLimiter();
        using var factory = CreateFactory(limiter);
        using var client = factory.CreateClient();

        using var registrationResponse = await client.PostAsJsonAsync(
            "/api/v1/identity/challenge",
            new RegistrationChallengeRequest("invalid"));
        Assert.Equal(HttpStatusCode.BadRequest, registrationResponse.StatusCode);

        using var peerResponse = await client.PostAsJsonAsync(
            "/api/v1/peer/lookup",
            new PeerLookupRequest("invalid", "invalid", 0, string.Empty, string.Empty));
        Assert.Equal(HttpStatusCode.BadRequest, peerResponse.StatusCode);

        Assert.Equal(
            [DirectoryRateLimitCategory.Registration, DirectoryRateLimitCategory.PeerOperations],
            limiter.Categories);
    }

    private static WebApplicationFactory<Program> CreateFactory(IGlobalRequestLimiter limiter)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IGlobalRequestLimiter>();
                    services.AddSingleton(limiter);
                });
            });

    private class CapturingLimiter : IGlobalRequestLimiter
    {
        public List<DirectoryRateLimitCategory> Categories { get; } = [];

        public virtual ValueTask<GlobalRateLimitDecision> AcquireAsync(
            DirectoryRateLimitCategory category,
            string clientPartitionKey,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Categories.Add(category);
            return ValueTask.FromResult(new GlobalRateLimitDecision(true, TimeSpan.Zero));
        }
    }

    private sealed class RejectingLimiter(TimeSpan retryAfter) : CapturingLimiter
    {
        public override ValueTask<GlobalRateLimitDecision> AcquireAsync(
            DirectoryRateLimitCategory category,
            string clientPartitionKey,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Categories.Add(category);
            return ValueTask.FromResult(new GlobalRateLimitDecision(false, retryAfter));
        }
    }
}
