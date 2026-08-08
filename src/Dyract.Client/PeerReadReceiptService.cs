using Dyract.Core.Identity;
using Dyract.Protocol;
using Dyract.Storage;

namespace Dyract.Client;

/// <summary>
/// Marks an already-delivered incoming message read and creates the peer-scoped DYRM read ACK.
/// Callers should invoke this only after the user has actually read/observed the message according
/// to the product UX policy; delivery alone never creates a read receipt.
/// </summary>
public sealed class PeerReadReceiptService
{
    private readonly IIncomingReadStore _incomingReadStore;
    private readonly TimeProvider _timeProvider;

    public PeerReadReceiptService(
        IIncomingReadStore incomingReadStore,
        TimeProvider? timeProvider = null)
    {
        _incomingReadStore = incomingReadStore ?? throw new ArgumentNullException(nameof(incomingReadStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<byte[]?> MarkReadAndCreateAckAsync(
        string messageId,
        PeerId localReaderPeerId,
        PeerId authenticatedRemoteSenderPeerId,
        DateTimeOffset? readAt = null,
        CancellationToken cancellationToken = default)
    {
        var timestamp = readAt ?? _timeProvider.GetUtcNow();
        var matched = await _incomingReadStore.MarkIncomingReadAsync(
            messageId,
            localReaderPeerId.Value,
            authenticatedRemoteSenderPeerId.Value,
            timestamp,
            cancellationToken);

        if (!matched)
        {
            return null;
        }

        return PeerMessagingProtocol.Encode(new PeerReadAckFrame(
            messageId,
            localReaderPeerId,
            authenticatedRemoteSenderPeerId,
            timestamp));
    }
}
