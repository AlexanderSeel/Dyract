using Dyract.Core.Identity;
using Dyract.Protocol;
using Dyract.Storage;
using Dyract.Transport;

namespace Dyract.App.Directory;

public sealed class DirectoryPeerSignalingGateway : IPeerSignalingGateway
{
    private readonly IDirectorySignalingService _signalingService;
    private readonly ILocalStore _localStore;

    public DirectoryPeerSignalingGateway(
        IDirectorySignalingService signalingService,
        ILocalStore localStore)
    {
        _signalingService = signalingService ?? throw new ArgumentNullException(nameof(signalingService));
        _localStore = localStore ?? throw new ArgumentNullException(nameof(localStore));
    }

    public async Task SendAsync(
        PeerId targetPeerId,
        string sessionId,
        string signalType,
        string payload,
        CancellationToken cancellationToken = default)
    {
        var contact = await _localStore.GetContactAsync(targetPeerId.Value, cancellationToken)
            ?? throw new InvalidOperationException("Target peer is not stored as a local contact.");

        await _signalingService.SendAsync(
            contact,
            sessionId,
            signalType,
            payload,
            cancellationToken);
    }

    public Task<IReadOnlyList<PeerSignalEnvelope>> FetchAsync(
        CancellationToken cancellationToken = default)
        => _signalingService.FetchAsync(cancellationToken);

    public Task AcknowledgeAsync(
        IEnumerable<string> signalIds,
        CancellationToken cancellationToken = default)
        => _signalingService.AcknowledgeAsync(signalIds, cancellationToken);
}
