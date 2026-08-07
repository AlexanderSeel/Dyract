using Dyract.Client;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Dyract.Storage;
using Xunit;

namespace Dyract.Tests;

public sealed class PeerMessageProcessorTests
{
    private const string MessageId = "019c1a2b3c4d7e8f9123456789abcdef";

    [Fact]
    public async Task TextFrame_IsStoredThenReturnsDeliveryAck()
    {
        using var local = PeerIdentity.Generate();
        using var remote = PeerIdentity.Generate();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_786_111_000_000);
        var deliveredAt = now.AddMilliseconds(-25);
        var incomingStore = new StubIncomingStore
        {
            Result = new IncomingMessageStoreResult(
                CreateStoredIncoming(remote.PeerId, local.PeerId, deliveredAt),
                true)
        };
        var deliveryStore = new StubOutgoingDeliveryStore();
        var processor = new PeerMessageProcessor(
            incomingStore,
            deliveryStore,
            new FixedTimeProvider(now));
        var wire = PeerMessagingProtocol.Encode(new PeerTextMessageFrame(
            MessageId,
            remote.PeerId,
            local.PeerId,
            now.AddSeconds(-1),
            "hello"));

        var result = await processor.ProcessIncomingAsync(wire, local.PeerId, remote.PeerId);

        Assert.Equal(PeerMessageProcessingKind.IncomingStored, result.Kind);
        Assert.Equal(1, incomingStore.CallCount);
        Assert.NotNull(result.ResponseFrame);
        Assert.True(PeerMessagingProtocol.TryDecode(result.ResponseFrame, out var decoded, out var error), error);
        var ack = Assert.IsType<PeerDeliveryAckFrame>(decoded);
        Assert.Equal(MessageId, ack.MessageId);
        Assert.Equal(local.PeerId, ack.SenderPeerId);
        Assert.Equal(remote.PeerId, ack.RecipientPeerId);
        Assert.Equal(deliveredAt, ack.DeliveredAt);
    }

    [Fact]
    public async Task DuplicateText_ReturnsAckAgain()
    {
        using var local = PeerIdentity.Generate();
        using var remote = PeerIdentity.Generate();
        var now = DateTimeOffset.UtcNow;
        var incomingStore = new StubIncomingStore
        {
            Result = new IncomingMessageStoreResult(
                CreateStoredIncoming(remote.PeerId, local.PeerId, now.AddSeconds(-1)),
                false)
        };
        var processor = new PeerMessageProcessor(
            incomingStore,
            new StubOutgoingDeliveryStore(),
            new FixedTimeProvider(now));
        var wire = PeerMessagingProtocol.Encode(new PeerTextMessageFrame(
            MessageId,
            remote.PeerId,
            local.PeerId,
            now.AddSeconds(-2),
            "hello"));

        var result = await processor.ProcessIncomingAsync(wire, local.PeerId, remote.PeerId);

        Assert.Equal(PeerMessageProcessingKind.IncomingDuplicate, result.Kind);
        Assert.NotNull(result.ResponseFrame);
        Assert.True(PeerMessagingProtocol.TryDecode(result.ResponseFrame, out var decoded, out _));
        Assert.IsType<PeerDeliveryAckFrame>(decoded);
    }

    [Fact]
    public async Task FrameFromDifferentPeer_IsRejectedBeforeStorage()
    {
        using var local = PeerIdentity.Generate();
        using var remote = PeerIdentity.Generate();
        using var mallory = PeerIdentity.Generate();
        var now = DateTimeOffset.UtcNow;
        var incomingStore = new StubIncomingStore();
        var processor = new PeerMessageProcessor(
            incomingStore,
            new StubOutgoingDeliveryStore(),
            new FixedTimeProvider(now));
        var wire = PeerMessagingProtocol.Encode(new PeerTextMessageFrame(
            MessageId,
            mallory.PeerId,
            local.PeerId,
            now,
            "wrong peer"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            processor.ProcessIncomingAsync(wire, local.PeerId, remote.PeerId));
        Assert.Equal(0, incomingStore.CallCount);
    }

    [Fact]
    public async Task DeliveryAck_UsesAuthenticatedPeerScope()
    {
        using var local = PeerIdentity.Generate();
        using var remote = PeerIdentity.Generate();
        var now = DateTimeOffset.UtcNow;
        var deliveryStore = new StubOutgoingDeliveryStore { ReturnValue = true };
        var processor = new PeerMessageProcessor(
            new StubIncomingStore(),
            deliveryStore,
            new FixedTimeProvider(now));
        var wire = PeerMessagingProtocol.Encode(new PeerDeliveryAckFrame(
            MessageId,
            remote.PeerId,
            local.PeerId,
            now));

        var result = await processor.ProcessIncomingAsync(wire, local.PeerId, remote.PeerId);

        Assert.Equal(PeerMessageProcessingKind.DeliveryAcknowledged, result.Kind);
        Assert.Equal(local.PeerId.Value, deliveryStore.LastSenderPeerId);
        Assert.Equal(remote.PeerId.Value, deliveryStore.LastRecipientPeerId);
        Assert.Equal(MessageId, deliveryStore.LastMessageId);
        Assert.Null(result.ResponseFrame);
    }

    [Fact]
    public async Task UnknownDeliveryAck_IsNonFatalAndProducesNoResponse()
    {
        using var local = PeerIdentity.Generate();
        using var remote = PeerIdentity.Generate();
        var now = DateTimeOffset.UtcNow;
        var processor = new PeerMessageProcessor(
            new StubIncomingStore(),
            new StubOutgoingDeliveryStore { ReturnValue = false },
            new FixedTimeProvider(now));
        var wire = PeerMessagingProtocol.Encode(new PeerDeliveryAckFrame(
            MessageId,
            remote.PeerId,
            local.PeerId,
            now));

        var result = await processor.ProcessIncomingAsync(wire, local.PeerId, remote.PeerId);

        Assert.Equal(PeerMessageProcessingKind.DeliveryAckUnknown, result.Kind);
        Assert.Null(result.ResponseFrame);
    }

    [Fact]
    public async Task MalformedFrame_IsRejectedBeforePersistence()
    {
        using var local = PeerIdentity.Generate();
        using var remote = PeerIdentity.Generate();
        var incomingStore = new StubIncomingStore();
        var processor = new PeerMessageProcessor(incomingStore, new StubOutgoingDeliveryStore());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            processor.ProcessIncomingAsync([0x01, 0x02, 0x03], local.PeerId, remote.PeerId));
        Assert.Equal(0, incomingStore.CallCount);
    }

    private static LocalMessage CreateStoredIncoming(
        Dyract.Core.Identity.PeerId remotePeerId,
        Dyract.Core.Identity.PeerId localPeerId,
        DateTimeOffset deliveredAt)
        => new(
            MessageId,
            "019c1a2b3c4d7e8f9123456789abcdea",
            remotePeerId.Value,
            localPeerId.Value,
            LocalMessageDirection.Incoming,
            LocalMessageType.Text,
            LocalMessageState.Delivered,
            deliveredAt.AddSeconds(-1),
            deliveredAt,
            null,
            "hello");

    private sealed class StubIncomingStore : IIncomingMessageStore
    {
        public IncomingMessageStoreResult? Result { get; init; }
        public int CallCount { get; private set; }

        public Task<IncomingMessageStoreResult> StoreIncomingTextAsync(
            string messageId,
            string senderPeerId,
            string recipientPeerId,
            string text,
            DateTimeOffset createdAt,
            DateTimeOffset receivedAt,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(Result ?? new IncomingMessageStoreResult(
                new LocalMessage(
                    messageId,
                    "019c1a2b3c4d7e8f9123456789abcdea",
                    senderPeerId,
                    recipientPeerId,
                    LocalMessageDirection.Incoming,
                    LocalMessageType.Text,
                    LocalMessageState.Delivered,
                    createdAt,
                    receivedAt,
                    null,
                    text),
                true));
        }
    }

    private sealed class StubOutgoingDeliveryStore : IOutgoingDeliveryStore
    {
        public bool ReturnValue { get; init; }
        public string? LastMessageId { get; private set; }
        public string? LastSenderPeerId { get; private set; }
        public string? LastRecipientPeerId { get; private set; }

        public Task<bool> MarkOutgoingDeliveredAsync(
            string messageId,
            string senderPeerId,
            string recipientPeerId,
            DateTimeOffset deliveredAt,
            CancellationToken cancellationToken = default)
        {
            LastMessageId = messageId;
            LastSenderPeerId = senderPeerId;
            LastRecipientPeerId = recipientPeerId;
            return Task.FromResult(ReturnValue);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
