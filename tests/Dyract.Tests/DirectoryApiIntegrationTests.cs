using System.Net;
using System.Net.Http.Json;
using Dyract.Client;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Dyract.Tests;

public sealed class DirectoryApiIntegrationTests
{
    [Fact]
    public async Task Health_ReturnsOk()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RegisteredPeer_CanLookupAnotherRegisteredPeer()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var httpClient = factory.CreateClient();
        var directory = new DirectoryClient(httpClient);
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();

        await directory.RegisterAsync(alice);
        await directory.RegisterAsync(bob);

        var lookup = await directory.LookupAsync(alice, bob.PeerId.Value);

        Assert.Equal(bob.PeerId.Value, lookup.PeerId);
        Assert.Equal(
            Convert.ToBase64String(bob.ExportPublicKey()),
            lookup.PublicKey);
    }

    [Fact]
    public async Task AuthorizedResolve_ReturnsPublishedPresence()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var httpClient = factory.CreateClient();
        var directory = new DirectoryClient(httpClient);
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();

        await directory.RegisterAsync(alice);
        await directory.RegisterAsync(bob);

        var capability = ContactCapabilityFactory.Create(
            bob,
            alice.PeerId.Value,
            TimeSpan.FromMinutes(10));

        var candidate = new ConnectionCandidate(
            Kind: "srflx",
            Protocol: "udp",
            Address: "203.0.113.20",
            Port: 45678,
            Priority: 100);

        await directory.PublishPresenceAsync(bob, new[] { candidate });

        var resolved = await directory.ResolveAsync(
            alice,
            bob.PeerId.Value,
            capability);

        Assert.True(resolved.IsReachable);
        Assert.Equal(bob.PeerId.Value, resolved.PeerId);
        Assert.Single(resolved.Candidates);
        Assert.Equal(candidate, resolved.Candidates[0]);
        Assert.NotNull(resolved.LeaseExpiresUnixSeconds);
    }

    [Fact]
    public async Task Resolve_WithCapabilityForAnotherGrantee_IsRejected()
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

        var capabilityForMallory = ContactCapabilityFactory.Create(
            bob,
            mallory.PeerId.Value,
            TimeSpan.FromMinutes(10));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            directory.ResolveAsync(
                alice,
                bob.PeerId.Value,
                capabilityForMallory));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [Fact]
    public async Task AuthorizedSignal_IsRetainedUntilTargetAcknowledgesIt()
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
        var sessionId = PeerSignalingClient.CreateSessionId();

        var sent = await signaling.SendAsync(
            alice,
            bob.PeerId.Value,
            capability,
            sessionId,
            PeerSignalTypes.Offer,
            "{\"type\":\"offer\",\"sdp\":\"opaque-test-offer\"}");

        var firstFetch = await signaling.FetchAsync(bob);
        Assert.Single(firstFetch.Signals);
        Assert.Equal(sent.SignalId, firstFetch.Signals[0].SignalId);
        Assert.Equal(alice.PeerId.Value, firstFetch.Signals[0].SenderPeerId);
        Assert.Equal(sessionId, firstFetch.Signals[0].SessionId);
        Assert.Equal(PeerSignalTypes.Offer, firstFetch.Signals[0].SignalType);

        var secondFetch = await signaling.FetchAsync(bob);
        Assert.Single(secondFetch.Signals);
        Assert.Equal(sent.SignalId, secondFetch.Signals[0].SignalId);

        await signaling.AckAsync(bob, new[] { sent.SignalId });

        var afterAck = await signaling.FetchAsync(bob);
        Assert.Empty(afterAck.Signals);
    }

    [Fact]
    public async Task Signal_WithCapabilityForAnotherGrantee_IsRejected()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var httpClient = factory.CreateClient();
        var directory = new DirectoryClient(httpClient);
        var signaling = new PeerSignalingClient(httpClient);
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        using var mallory = PeerIdentity.Generate();

        await directory.RegisterAsync(alice);
        await directory.RegisterAsync(bob);
        await directory.RegisterAsync(mallory);

        var capabilityForMallory = ContactCapabilityFactory.Create(
            bob,
            mallory.PeerId.Value,
            TimeSpan.FromMinutes(10));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => signaling.SendAsync(
            alice,
            bob.PeerId.Value,
            capabilityForMallory,
            PeerSignalingClient.CreateSessionId(),
            PeerSignalTypes.Offer,
            "opaque"));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [Fact]
    public async Task OversizedRequest_IsRejectedBeforeEndpointProcessing()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var content = new ByteArrayContent(new byte[65 * 1024]);
        content.Headers.ContentType = new("application/json");

        using var response = await client.PostAsync(
            "/api/v1/identity/challenge",
            content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task RegistrationEndpoint_IsRateLimitedPerClient()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        HttpResponseMessage? lastResponse = null;

        for (var i = 0; i < 31; i++)
        {
            lastResponse?.Dispose();
            lastResponse = await client.PostAsJsonAsync(
                "/api/v1/identity/challenge",
                new RegistrationChallengeRequest("invalid"));
        }

        using (lastResponse)
        {
            Assert.NotNull(lastResponse);
            Assert.Equal(HttpStatusCode.TooManyRequests, lastResponse.StatusCode);
        }
    }
}
