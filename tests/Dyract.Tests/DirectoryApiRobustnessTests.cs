using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Dyract.Tests;

public sealed class DirectoryApiRobustnessTests
{
    private static readonly string[] PostEndpoints =
    [
        "/api/v1/identity/challenge",
        "/api/v1/identity/register",
        "/api/v1/peer/lookup",
        "/api/v1/presence",
        "/api/v1/presence/remove",
        "/api/v1/capability/revoke",
        "/api/v1/peer/resolve",
        "/api/v1/signal/send",
        "/api/v1/signal/fetch",
        "/api/v1/signal/ack"
    ];

    [Fact]
    public async Task MalformedJsonAndBinaryBodies_DoNotProduceServerErrors()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var random = new Random(0x41504946); // "APIF"

        foreach (var endpoint in PostEndpoints)
        {
            for (var iteration = 0; iteration < 12; iteration++)
            {
                var body = new byte[random.Next(0, 4096)];
                random.NextBytes(body);

                // Periodically make the first bytes look JSON-like so the corpus covers both
                // immediate UTF-8/JSON rejection and deeper model-binding failures.
                if (body.Length >= 2 && iteration % 3 == 0)
                {
                    body[0] = (byte)'{';
                    body[^1] = (byte)'}';
                }
                else if (body.Length >= 2 && iteration % 3 == 1)
                {
                    body[0] = (byte)'[';
                    body[^1] = (byte)']';
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new ByteArrayContent(body)
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                using var response = await client.SendAsync(request);
                Assert.True(
                    (int)response.StatusCode < 500,
                    $"Malformed request unexpectedly produced {(int)response.StatusCode} for {endpoint} at deterministic iteration {iteration}.");
            }
        }
    }

    [Fact]
    public async Task OversizedBodies_AreRejectedAtOuterBoundary()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var body = new byte[70 * 1024];

        foreach (var endpoint in PostEndpoints)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(body)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
    }
}
