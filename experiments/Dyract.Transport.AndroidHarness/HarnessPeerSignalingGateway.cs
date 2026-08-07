using System.Security.Cryptography;
using Dyract.Client;
using Dyract.Core.Identity;
using Dyract.Protocol;
using Dyract.Transport;

namespace Dyract.Transport.AndroidHarness;

public sealed class HarnessPeerSignalingGateway : IPeerSignalingGateway, IDisposable
{
    private readonly HarnessIdentityVault _identityVault;
    private readonly PeerId _remotePeerId;
    private readonly byte[] _remotePublicKey;
    private readonly ContactCapability _capability;
    private readonly HttpClient _httpClient;
    private readonly PeerSignalingClient _signalingClient;

    public HarnessPeerSignalingGateway(
        Uri directoryBaseUri,
        HarnessIdentityVault identityVault,
        PeerId remotePeerId,
        byte[] remotePublicKey,
        ContactCapability capability)
    {
        ArgumentNullException.ThrowIfNull(directoryBaseUri);
        _identityVault = identityVault ?? throw new ArgumentNullException(nameof(identityVault));
        _remotePeerId = remotePeerId;
        _remotePublicKey = remotePublicKey?.ToArray() ?? throw new ArgumentNullException(nameof(remotePublicKey));
        _capability = capability ?? throw new ArgumentNullException(nameof(capability));
        _httpClient = new HttpClient
        {
            BaseAddress = directoryBaseUri,
            Timeout = TimeSpan.FromSeconds(20)
        };
        _signalingClient = new PeerSignalingClient(_httpClient);
    }

    public async Task SendAsync(
        PeerId targetPeerId,
        string sessionId,
        string signalType,
        string payload,
        CancellationToken cancellationToken = default)
    {
        if (targetPeerId != _remotePeerId)
        {
            throw new InvalidOperationException("Diagnostic signaling gateway is scoped to a different remote peer.");
        }

        using var identity = await _identityVault.GetOrCreateAsync(cancellationToken);
        if (!ContactCapabilityVerifier.TryVerify(
                _capability,
                _remotePublicKey,
                identity.PeerId.Value,
                out var verificationError))
        {
            throw new InvalidOperationException(
                verificationError ?? "Remote pairing capability is invalid or expired.");
        }

        await _signalingClient.SendAsync(
            identity,
            _remotePeerId.Value,
            _capability,
            sessionId,
            signalType,
            payload,
            cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<PeerSignalEnvelope>> FetchAsync(
        CancellationToken cancellationToken = default)
    {
        using var identity = await _identityVault.GetOrCreateAsync(cancellationToken);
        var response = await _signalingClient.FetchAsync(identity, cancellationToken);
        return response.Signals;
    }

    public async Task AcknowledgeAsync(
        IEnumerable<string> signalIds,
        CancellationToken cancellationToken = default)
    {
        using var identity = await _identityVault.GetOrCreateAsync(cancellationToken);
        await _signalingClient.AckAsync(identity, signalIds, cancellationToken);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_remotePublicKey);
        _httpClient.Dispose();
    }
}
