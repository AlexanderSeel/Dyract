using System.Net.Http.Json;
using System.Security;
using System.Security.Cryptography;
using Dyract.Crypto.Identity;
using Dyract.Protocol;

namespace Dyract.Client;

public sealed class DirectoryClient
{
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
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
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
                response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: cancellationToken)
            ?? throw new HttpRequestException("Dyract directory returned an empty response body.");
    }
}
