using Dyract.Core.Identity;
using Dyract.Protocol;

namespace Dyract.Transport;

public interface IPeerSignalingGateway
{
    Task SendAsync(
        PeerId targetPeerId,
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
