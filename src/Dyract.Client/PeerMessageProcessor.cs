using Dyract.Core.Identity;
using Dyract.Protocol;
using Dyract.Storage;

namespace Dyract.Client;

public enum PeerMessageProcessingKind
{
    IncomingStored = 1,
    IncomingDuplicate = 2,
    DeliveryAcknowledged = 3,
    DeliveryAckUnknown = 4,
    ReadAcknowledged = 5,
    ReadAckUnknown = 6
}

public sealed record PeerMessageProcessingResult(
    PeerMessageProcessingKind Kind,
    string MessageId,
    byte[]? ResponseFrame = null);

public sealed class PeerMessageProcessor
{
    private readonly IIncomingMessageStore _incomingStore;
    private readonly IOutgoingDeliveryStore _outgoingDeliveryStore;
    private readonly IOutgoingReadStore _outgoingReadStore;
    private readonly TimeProvider _timeProvider;

    public PeerMessageProcessor(
        IIncomingMessageStore incomingStore,
        IOutgoingDeliveryStore outgoingDeliveryStore,
        IOutgoingReadStore outgoingReadStore,
        TimeProvider? timeProvider = null)
    {
        _incomingStore = incomingStore ?? throw new ArgumentNullException(nameof(incomingStore));
        _outgoingDeliveryStore = outgoingDeliveryStore ?? throw new ArgumentNullException(nameof(outgoingDeliveryStore));
        _outgoingReadStore = outgoingReadStore ?? throw new ArgumentNullException(nameof(outgoingReadStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PeerMessageProcessingResult> ProcessIncomingAsync(
        ReadOnlyMemory<byte> plaintextFrame,
        PeerId localPeerId,
        PeerId authenticatedRemotePeerId,
        CancellationToken cancellationToken = default)
    {
        if (plaintextFrame.IsEmpty)
        {
            throw new ArgumentException("Peer message frame must not be empty.", nameof(plaintextFrame));
        }

        if (!PeerMessagingProtocol.TryDecode(
                plaintextFrame.Span,
                out var frame,
                out var decodeError) ||
            frame is null)
        {
            throw new InvalidDataException(decodeError ?? "Peer message frame could not be decoded.");
        }

        var now = _timeProvider.GetUtcNow();
        if (!PeerMessagingProtocol.TryValidateForReceiver(
                frame,
                localPeerId,
                authenticatedRemotePeerId,
                now,
                out var validationError))
        {
            throw new InvalidDataException(
                validationError ?? "Peer message frame failed authenticated-session scope validation.");
        }

        switch (frame)
        {
            case PeerTextMessageFrame text:
                return await ProcessTextAsync(text, now, cancellationToken);

            case PeerDeliveryAckFrame ack:
                return await ProcessDeliveryAckAsync(
                    ack,
                    localPeerId,
                    authenticatedRemotePeerId,
                    cancellationToken);

            case PeerReadAckFrame readAck:
                return await ProcessReadAckAsync(
                    readAck,
                    localPeerId,
                    authenticatedRemotePeerId,
                    cancellationToken);

            default:
                throw new NotSupportedException($"Peer application frame {frame.GetType().Name} is not supported.");
        }
    }

    private async Task<PeerMessageProcessingResult> ProcessTextAsync(
        PeerTextMessageFrame text,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        var stored = await _incomingStore.StoreIncomingTextAsync(
            text.MessageId,
            text.SenderPeerId.Value,
            text.RecipientPeerId.Value,
            text.Text,
            text.CreatedAt,
            receivedAt,
            cancellationToken);

        var durableDeliveredAt = stored.Message.DeliveredAt ?? receivedAt;
        var ack = PeerMessagingProtocol.CreateDeliveryAck(text, durableDeliveredAt);
        return new PeerMessageProcessingResult(
            stored.IsNew
                ? PeerMessageProcessingKind.IncomingStored
                : PeerMessageProcessingKind.IncomingDuplicate,
            text.MessageId,
            PeerMessagingProtocol.Encode(ack));
    }

    private async Task<PeerMessageProcessingResult> ProcessDeliveryAckAsync(
        PeerDeliveryAckFrame ack,
        PeerId localPeerId,
        PeerId authenticatedRemotePeerId,
        CancellationToken cancellationToken)
    {
        var found = await _outgoingDeliveryStore.MarkOutgoingDeliveredAsync(
            ack.MessageId,
            localPeerId.Value,
            authenticatedRemotePeerId.Value,
            ack.DeliveredAt,
            cancellationToken);

        return new PeerMessageProcessingResult(
            found
                ? PeerMessageProcessingKind.DeliveryAcknowledged
                : PeerMessageProcessingKind.DeliveryAckUnknown,
            ack.MessageId);
    }

    private async Task<PeerMessageProcessingResult> ProcessReadAckAsync(
        PeerReadAckFrame ack,
        PeerId localPeerId,
        PeerId authenticatedRemotePeerId,
        CancellationToken cancellationToken)
    {
        var found = await _outgoingReadStore.MarkOutgoingReadAsync(
            ack.MessageId,
            localPeerId.Value,
            authenticatedRemotePeerId.Value,
            ack.ReadAt,
            cancellationToken);

        return new PeerMessageProcessingResult(
            found
                ? PeerMessageProcessingKind.ReadAcknowledged
                : PeerMessageProcessingKind.ReadAckUnknown,
            ack.MessageId);
    }
}
