using System.Security;
using Dyract.App.Security;
using Dyract.Client;
using Dyract.Protocol;
using Dyract.Storage;

namespace Dyract.App.Directory;

public sealed record DirectoryRegistrationResult(
    Uri BaseUri,
    string PeerId,
    DateTimeOffset RegisteredAt);

public sealed record DirectoryReachabilityResult(
    Uri BaseUri,
    string PeerId,
    bool IsReachable,
    IReadOnlyList<ConnectionCandidate> Candidates,
    DateTimeOffset? LeaseExpiresAt);

public interface IDirectoryService
{
    Uri? ConfiguredBaseUri { get; }
    Uri Configure(string value);
    Task<DirectoryRegistrationResult> RegisterAsync(CancellationToken cancellationToken = default);
    Task<DirectoryReachabilityResult> ResolveAsync(LocalContact contact, CancellationToken cancellationToken = default);
    Task RevokeIssuedCapabilityAsync(ContactCapability capability, CancellationToken cancellationToken = default);
}

public sealed class DirectoryService : IDirectoryService, IDisposable
{
    private readonly IDirectorySettingsStore _settings;
    private readonly IIdentityVault _identityVault;
    private readonly object _clientGate = new();
    private HttpClient? _httpClient;
    private Uri? _clientBaseUri;

    public DirectoryService(IDirectorySettingsStore settings, IIdentityVault identityVault)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _identityVault = identityVault ?? throw new ArgumentNullException(nameof(identityVault));
    }

    public Uri? ConfiguredBaseUri => _settings.GetBaseUri();

    public Uri Configure(string value)
    {
        var uri = _settings.SetBaseUri(value);
        lock (_clientGate)
        {
            if (_clientBaseUri != uri)
            {
                _httpClient?.Dispose();
                _httpClient = null;
                _clientBaseUri = null;
            }
        }

        return uri;
    }

    public async Task<DirectoryRegistrationResult> RegisterAsync(CancellationToken cancellationToken = default)
    {
        var baseUri = RequireBaseUri();
        using var identity = await _identityVault.GetOrCreateAsync(cancellationToken);
        var response = await GetClient(baseUri).RegisterAsync(identity, cancellationToken);

        if (!string.Equals(response.PeerId, identity.PeerId.Value, StringComparison.Ordinal))
        {
            throw new SecurityException("Directory registration returned a different PeerId.");
        }

        DateTimeOffset registeredAt;
        try
        {
            registeredAt = DateTimeOffset.FromUnixTimeSeconds(response.RegisteredUnixSeconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new SecurityException("Directory registration returned an invalid timestamp.", exception);
        }

        return new DirectoryRegistrationResult(baseUri, response.PeerId, registeredAt);
    }

    public async Task<DirectoryReachabilityResult> ResolveAsync(
        LocalContact contact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var baseUri = RequireBaseUri();

        if (string.IsNullOrWhiteSpace(contact.Capability))
        {
            throw new SecurityException("This contact does not have a stored pairing response.");
        }

        if (!ContactPairingCodec.TryDecode(
                contact.Capability,
                out var capability,
                out var decodeError) ||
            capability is null)
        {
            throw new SecurityException(decodeError ?? "This contact does not have a valid stored pairing response.");
        }

        using var identity = await _identityVault.GetOrCreateAsync(cancellationToken);
        if (!ContactCapabilityVerifier.TryVerify(
                capability,
                contact.IdentityPublicKey,
                identity.PeerId.Value,
                out var verificationError))
        {
            throw new SecurityException(verificationError ?? "Stored pairing response could not be verified.");
        }

        var response = await GetClient(baseUri).ResolveAsync(
            identity,
            contact.PeerId,
            capability,
            cancellationToken);

        if (!string.Equals(response.PeerId, contact.PeerId, StringComparison.Ordinal))
        {
            throw new SecurityException("Directory resolved a different PeerId than requested.");
        }

        byte[] returnedPublicKey;
        try
        {
            returnedPublicKey = Convert.FromBase64String(response.PublicKey);
        }
        catch (FormatException exception)
        {
            throw new SecurityException("Directory returned malformed public-key material.", exception);
        }

        if (!returnedPublicKey.AsSpan().SequenceEqual(contact.IdentityPublicKey))
        {
            throw new SecurityException("Directory returned public-key material that differs from the pinned contact identity.");
        }

        DateTimeOffset? leaseExpiresAt = null;
        if (response.LeaseExpiresUnixSeconds is long expiry)
        {
            try
            {
                leaseExpiresAt = DateTimeOffset.FromUnixTimeSeconds(expiry);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new SecurityException("Directory returned an invalid presence lease expiry.", exception);
            }
        }

        return new DirectoryReachabilityResult(
            baseUri,
            response.PeerId,
            response.IsReachable,
            response.Candidates,
            leaseExpiresAt);
    }

    public async Task RevokeIssuedCapabilityAsync(
        ContactCapability capability,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        var baseUri = RequireBaseUri();
        using var identity = await _identityVault.GetOrCreateAsync(cancellationToken);
        await GetClient(baseUri).RevokeContactCapabilityAsync(identity, capability, cancellationToken);
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

    private Uri RequireBaseUri()
        => ConfiguredBaseUri ?? throw new InvalidOperationException("Configure an HTTPS Dyract directory URL first.");

    private Dyract.Client.DirectoryClient GetClient(Uri baseUri)
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

            return new Dyract.Client.DirectoryClient(_httpClient);
        }
    }
}
