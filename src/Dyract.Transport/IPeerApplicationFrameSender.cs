using Dyract.Core.Identity;

namespace Dyract.Transport;

public interface IPeerApplicationFrameSender
{
    Task SendAsync(
        PeerId recipientPeerId,
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken = default);
}
