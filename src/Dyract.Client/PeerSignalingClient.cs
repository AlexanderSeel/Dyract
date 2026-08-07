using System.Net.Http.Json;
using System.Security.Cryptography;
using Dyract.Crypto.Identity;
using Dyract.Protocol;

namespace Dyract.Client;

public sealed class PeerSignalingClient
{
    private static readonly TimeSpan DefaultSignalLifetime = TimeSpan.FromSeconds(45);
    private readonly HttpClient _httpClient;

    public PeerSignalingClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<SendPeerSignalResponse> SendAsync(
        PeerIdentity sender,
        string targetPeerId,
        ContactCapability capability,
        string sessionId,
        string signalType,
        string payload,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPeerId);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(signalType);
        ArgumentNullException.ThrowIfNull(payload);

        var signalLifetime = lifetime ?? DefaultSignalLifetime;
        if (signalLifetime <= TimeSpan.Zero || signalLifetime > TimeSpan.FromSeconds(60))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Signal lifetime must be positive and at most 60 seconds.");
        }

        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(signalLifetime).ToUnixTimeSeconds();
        var timestamp = now.ToUnixTimeSeconds();
        var nonce = CreateNonce();
        var proof = ProofPayload.ForSignalSend(
            sender.PeerId.Value,
            targetPeerId,
            capability.CapabilityId,
            sessionId,
            signalType,
            payload,
            expires,
            timestamp,
            nonce);
        var signature = Convert.ToBase64String(sender.Sign(proof));

        return await PostAsync<SendPeerSignalRequest, SendPeerSignalResponse>(
            "/api/v1/signal/send",
            new SendPeerSignalRequest(
                sender.PeerId.Value,
                targetPeerId,
                capability,
                sessionId,
                signalType,
                payload,
                expires,
                timestamp,
                nonce,
                signature),
            cancellationToken);
    }

    public async Task<FetchPeerSignalsResponse> FetchAsync(
        PeerIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = CreateNonce();
        var proof = ProofPayload.ForSignalFetch(identity.PeerId.Value, timestamp, nonce);
        var signature = Convert.ToBase64String(identity.Sign(proof));

        return await PostAsync<FetchPeerSignalsRequest, FetchPeerSignalsResponse>(
            "/api/v1/signal/fetch",
            new FetchPeerSignalsRequest(
                identity.PeerId.Value,
                timestamp,
                nonce,
                signature),
            cancellationToken);
    }

    public async Task AckAsync(
        PeerIdentity identity,
        IEnumerable<string> signalIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(signalIds);
        var ids = signalIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = CreateNonce();
        var proof = ProofPayload.ForSignalAck(identity.PeerId.Value, ids, timestamp, nonce);
        var signature = Convert.ToBase64String(identity.Sign(proof));

        using var response = await _httpClient.PostAsJsonAsync(
            "/api/v1/signal/ack",
            new AckPeerSignalsRequest(
                identity.PeerId.Value,
                ids,
                timestamp,
                nonce,
                signature),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Dyract signaling request failed with {(int)response.StatusCode} {response.StatusCode}: {body}",
                inner: null,
                statusCode: response.StatusCode);
        }
    }

    public static string CreateSessionId()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

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
                $"Dyract signaling request failed with {(int)response.StatusCode} {response.StatusCode}: {body}",
                inner: null,
                statusCode: response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: cancellationToken)
            ?? throw new HttpRequestException("Dyract signaling endpoint returned an empty response body.");
    }
}
