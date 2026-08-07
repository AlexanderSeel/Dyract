using System.Net.Http.Json;
using System.Security;
using System.Security.Cryptography;
using Dyract.Crypto.Identity;
using Dyract.Protocol;

namespace Dyract.Client;

public sealed class DirectoryClient
{
    private static readonly TimeSpan DefaultPresenceLifetime = TimeSpan.FromSeconds(90);
    private readonly HttpClient _httpClient;

    public DirectoryClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<RegisterPeerResponse> RegisterAsync(
        PeerIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var publicKey = Convert.ToBase64String(identity.ExportPublicKey());
        var challenge = await PostAsync<RegistrationChallengeRequest, RegistrationChallengeResponse>(
            "/api/v1/identity/challenge",
            new RegistrationChallengeRequest(publicKey),
            cancellationToken);

        if (!string.Equals(challenge.PeerId, identity.PeerId.Value, StringComparison.Ordinal))
        {
            throw new SecurityException("Directory returned a Peer ID that does not match the local identity.");
        }

        var proof = ProofPayload.ForRegistration(
            challenge.ChallengeId,
            challenge.PeerId,
            publicKey,
            challenge.Challenge);

        var signature = Convert.ToBase64String(identity.Sign(proof));

        return await PostAsync<RegisterPeerRequest, RegisterPeerResponse>(
            "/api/v1/identity/register",
            new RegisterPeerRequest(
                identity.PeerId.Value,
                publicKey,
                challenge.ChallengeId,
                signature),
            cancellationToken);
    }

    public async Task<PeerLookupResponse> LookupAsync(
        PeerIdentity requester,
        string targetPeerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPeerId);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = CreateNonce();
        var proof = ProofPayload.ForLookup(
            requester.PeerId.Value,
            targetPeerId,
            timestamp,
            nonce);

        var signature = Convert.ToBase64String(requester.Sign(proof));

        return await PostAsync<PeerLookupRequest, PeerLookupResponse>(
            "/api/v1/peer/lookup",
            new PeerLookupRequest(
                requester.PeerId.Value,
                targetPeerId,
                timestamp,
                nonce,
                signature),
            cancellationToken);
    }

    public async Task<PublishPresenceResponse> PublishPresenceAsync(
        PeerIdentity identity,
        IEnumerable<ConnectionCandidate> candidates,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(candidates);

        var candidateArray = candidates.ToArray();
        if (candidateArray.Length == 0)
        {
            throw new ArgumentException("At least one connection candidate is required.", nameof(candidates));
        }

        var leaseLifetime = lifetime ?? DefaultPresenceLifetime;
        if (leaseLifetime <= TimeSpan.Zero || leaseLifetime > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Presence lifetime must be greater than zero and at most two minutes.");
        }

        var now = DateTimeOffset.UtcNow;
        var leaseExpires = now.Add(leaseLifetime).ToUnixTimeSeconds();
        var timestamp = now.ToUnixTimeSeconds();
        var nonce = CreateNonce();
        var proof = ProofPayload.ForPresence(
            identity.PeerId.Value,
            candidateArray,
            leaseExpires,
            timestamp,
            nonce);

        var signature = Convert.ToBase64String(identity.Sign(proof));

        return await PostAsync<PublishPresenceRequest, PublishPresenceResponse>(
            "/api/v1/presence",
            new PublishPresenceRequest(
                identity.PeerId.Value,
                candidateArray,
                leaseExpires,
                timestamp,
                nonce,
                signature),
            cancellationToken);
    }

    public async Task RemovePresenceAsync(
        PeerIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = CreateNonce();
        var proof = ProofPayload.ForPresenceRemoval(identity.PeerId.Value, timestamp, nonce);
        var signature = Convert.ToBase64String(identity.Sign(proof));

        await PostNoContentAsync(
            "/api/v1/presence/remove",
            new RemovePresenceRequest(
                identity.PeerId.Value,
                timestamp,
                nonce,
                signature),
            cancellationToken);
    }

    public async Task<ResolvePeerResponse> ResolveAsync(
        PeerIdentity requester,
        string targetPeerId,
        ContactCapability capability,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPeerId);
        ArgumentNullException.ThrowIfNull(capability);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = CreateNonce();
        var proof = ProofPayload.ForResolve(
            requester.PeerId.Value,
            targetPeerId,
            capability.CapabilityId,
            timestamp,
            nonce);

        var signature = Convert.ToBase64String(requester.Sign(proof));

        return await PostAsync<ResolvePeerRequest, ResolvePeerResponse>(
            "/api/v1/peer/resolve",
            new ResolvePeerRequest(
                requester.PeerId.Value,
                targetPeerId,
                capability,
                timestamp,
                nonce,
                signature),
            cancellationToken);
    }

    private static string CreateNonce()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string uri,
        TRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(uri, request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Dyract directory request failed with {(int)response.StatusCode} {response.StatusCode}: {body}",
                inner: null,
                statusCode: response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: cancellationToken)
            ?? throw new HttpRequestException("Dyract directory returned an empty response body.");
    }

    private async Task PostNoContentAsync<TRequest>(
        string uri,
        TRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(uri, request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Dyract directory request failed with {(int)response.StatusCode} {response.StatusCode}: {body}",
                inner: null,
                statusCode: response.StatusCode);
        }
    }
}
