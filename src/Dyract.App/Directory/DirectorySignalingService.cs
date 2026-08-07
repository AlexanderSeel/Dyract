using System.Security;
using Dyract.App.Security;
using Dyract.Client;
using Dyract.Protocol;
using Dyract.Storage;

namespace Dyract.App.Directory;

public interface IDirectorySignalingService
{
    Task<SendPeerSignalResponse> SendAsync(
        LocalContact target,
        string sessionId,
        string signalType,
        string payload,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PeerSignalEnvelope>> FetchAsync(
        CancellationToken cancellationToken = default);

    Task AcknowledgeAsync(
        IEnumerable<string> signalIds,
        CancellationToken cancellationToken = default);
}

public sealed class DirectorySignalingService : IDirectorySignalingService, IDisposable
{
    private readonly IDirectorySettingsStore _settings;
    private readonly IIdentityVault _identityVault;
    private readonly object _clientGate = new();
    private HttpClient? _httpClient;
    private Uri? _clientBaseUri;

    public DirectorySignalingService(
        IDirectorySettingsStore settings,
        IIdentityVault identityVault)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _identityVault = identityVault ?? throw new ArgumentNullException(nameof(identityVault));
    }

    public async Task<SendPeerSignalResponse> SendAsync(
        LocalContact target,
        string sessionId,
        string signalType,
        string payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(signalType);
        ArgumentNullException.ThrowIfNull(payload);

        var baseUri = RequireBaseUri();
        using var identity = await _identityVault.GetOrCreateAsync(cancellationToken);
        var capability = GetVerifiedCapability(target, identity.PeerId.Value);

        return await GetClient(baseUri).SendAsync(
            identity,
            target.PeerId,
            capability,
            sessionId,
            signalType,
            payload,
            cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<PeerSignalEnvelope>> FetchAsync(
        CancellationToken cancellationToken = default)
    {
        var baseUri = RequireBaseUri();
        using var identity = await _identityVault.GetOrCreateAsync(cancellationToken);
        var response = await GetClient(baseUri).FetchAsync(identity, cancellationToken);
        return response.Signals;
    }

    public async Task AcknowledgeAsync(
        IEnumerable<string> signalIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signalIds);
        var ids = signalIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        var baseUri = RequireBaseUri();
        using var identity = await _identityVault.GetOrCreateAsync(cancellationToken);
        await GetClient(baseUri).AckAsync(identity, ids, cancellationToken);
    }

    public void Dispose()
    {
        lock (_clientGate)
        {
            _httpClient?.Dispose();
            _httpClient = null;
            _clientBaseUri = null;
        }
    }

    private static ContactCapability GetVerifiedCapability(LocalContact target, string localPeerId)
    {
        if (string.IsNullOrWhiteSpace(target.Capability))
        {
            throw new SecurityException("Target does not have a stored pairing response.");
        }

        if (!ContactPairingCodec.TryDecode(
                target.Capability,
                out var capability,
                out var decodeError) ||
            capability is null)
        {
            throw new SecurityException(decodeError ?? "Target does not have a valid stored pairing response.");
        }

        if (!ContactCapabilityVerifier.TryVerify(
                capability,
                target.IdentityPublicKey,
                localPeerId,
                out var verificationError))
        {
            throw new SecurityException(verificationError ?? "Target pairing response could not be verified.");
        }

        return capability;
    }

    private Uri RequireBaseUri()
        => _settings.GetBaseUri() ?? throw new InvalidOperationException("Configure an HTTPS Dyract directory URL first.");

    private PeerSignalingClient GetClient(Uri baseUri)
    {
        lock (_clientGate)
        {
            if (_httpClient is null || _clientBaseUri != baseUri)
            {
                _httpClient?.Dispose();
                _httpClient = new HttpClient
                {
                    BaseAddress = baseUri,
                    Timeout = TimeSpan.FromSeconds(20)
                };
                _clientBaseUri = baseUri;
            }

            return new PeerSignalingClient(_httpClient);
        }
    }
}
