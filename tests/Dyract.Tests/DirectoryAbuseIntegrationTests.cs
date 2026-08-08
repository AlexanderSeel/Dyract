using System.Net;
using System.Net.Http.Json;
using Dyract.Client;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Dyract.Tests;

public sealed class DirectoryAbuseIntegrationTests
{
    [Fact]
    public async Task UnregisteredPeer_CannotLookupKnownRegisteredTarget()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var httpClient = factory.CreateClient();
        var directory = new DirectoryClient(httpClient);
        using var attacker = PeerIdentity.Generate();
        using var target = PeerIdentity.Generate();

        await directory.RegisterAsync(target);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            directory.LookupAsync(attacker, target.PeerId.Value));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Contains("requester_unknown", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingCapability_NeverDisclosesPublishedReachability()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var httpClient = factory.CreateClient();
        var directory = new DirectoryClient(httpClient);
        using var requester = PeerIdentity.Generate();
        using var target = PeerIdentity.Generate();

        await directory.RegisterAsync(requester);
        await directory.RegisterAsync(target);
        await directory.PublishPresenceAsync(
            target,
            [new ConnectionCandidate("srflx", "udp", "203.0.113.77", 45678, 100)]);

        var request = new ResolvePeerRequest(
            requester.PeerId.Value,
            target.PeerId.Value,
            Capability: null!,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Convert.ToBase64String(new byte[24]),
            "invalid");

        using var response = await httpClient.PostAsJsonAsync("/api/v1/peer/resolve", request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("capability_missing", body, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.77", body, StringComparison.Ordinal);
        Assert.DoesNotContain("45678", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopiedCapability_CannotBeUsedByDifferentRegisteredPeer()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var httpClient = factory.CreateClient();
        var directory = new DirectoryClient(httpClient);
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        using var mallory = PeerIdentity.Generate();

        await directory.RegisterAsync(alice);
        await directory.RegisterAsync(bob);
        await directory.RegisterAsync(mallory);

        var capabilityForAlice = ContactCapabilityFactory.Create(
            bob,
            alice.PeerId.Value,
            TimeSpan.FromMinutes(10));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            directory.ResolveAsync(mallory, bob.PeerId.Value, capabilityForAlice));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Contains("capability_scope", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedSignal_IsRejectedAndNeverAppearsInTargetInbox()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var httpClient = factory.CreateClient();
        var directory = new DirectoryClient(httpClient);
        var signaling = new PeerSignalingClient(httpClient);
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();

        await directory.RegisterAsync(alice);
        await directory.RegisterAsync(bob);

        var capability = ContactCapabilityFactory.Create(
            bob,
            alice.PeerId.Value,
            TimeSpan.FromMinutes(10));
        var payload = new string('x', (32 * 1024) + 1);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => signaling.SendAsync(
            alice,
            bob.PeerId.Value,
            capability,
            PeerSignalingClient.CreateSessionId(),
            PeerSignalTypes.Offer,
            payload));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("invalid_signal_payload", exception.Message, StringComparison.Ordinal);
        Assert.Empty((await signaling.FetchAsync(bob)).Signals);
    }

    [Fact]
    public async Task PeerOperationEndpoint_IsRateLimitedPerClientAddress()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var invalidLookup = new PeerLookupRequest(
            "invalid",
            "invalid",
            0,
            string.Empty,
            string.Empty);

        HttpResponseMessage? lastResponse = null;
        for (var index = 0; index < 241; index++)
        {
            lastResponse?.Dispose();
            lastResponse = await client.PostAsJsonAsync("/api/v1/peer/lookup", invalidLookup);
        }

        using (lastResponse)
        {
            Assert.NotNull(lastResponse);
            Assert.Equal(HttpStatusCode.TooManyRequests, lastResponse.StatusCode);
        }
    }
}
