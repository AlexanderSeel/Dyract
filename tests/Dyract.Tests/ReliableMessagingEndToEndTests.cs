using Dyract.Client;
using Dyract.Core.Identity;
using Dyract.Crypto.Identity;
using Dyract.Storage;
using Xunit;

namespace Dyract.Tests;

public sealed class ReliableMessagingEndToEndTests
{
    [Fact]
    public async Task LostFirstAck_ResendsSameMessage_DeduplicatesReceiver_ThenClearsOutbox()
    {
        var aliceDatabase = CreateDatabasePath("alice");
        var bobDatabase = CreateDatabasePath("bob");

        try
        {
            using var aliceIdentity = PeerIdentity.Generate();
            using var bobIdentity = PeerIdentity.Generate();
            var aliceKey = new FixedKeyProvider(0x61);
            var bobKey = new FixedKeyProvider(0x62);
            var aliceStore = new SqliteLocalStore(aliceDatabase, aliceKey);
            var bobStore = new SqliteLocalStore(bobDatabase, bobKey);
            await aliceStore.InitializeAsync();
            await bobStore.InitializeAsync();

            await aliceStore.UpsertContactAsync(new ContactDraft(
                bobIdentity.PeerId.Value,
                bobIdentity.ExportPublicKey(),
                "Bob"));
            await bobStore.UpsertContactAsync(new ContactDraft(
                aliceIdentity.PeerId.Value,
                aliceIdentity.ExportPublicKey(),
                "Alice"));

            var aliceReliability = new SqliteIncomingMessageStore(aliceDatabase, aliceKey, aliceStore);
            var bobReliability = new SqliteIncomingMessageStore(bobDatabase, bobKey, bobStore);
            var aliceProcessor = new PeerMessageProcessor(aliceReliability, aliceReliability);
            var bobProcessor = new PeerMessageProcessor(bobReliability, bobReliability);
            var aliceOutbox = new SqliteOutboxDeliveryQueue(aliceDatabase, aliceKey, aliceStore);

            var clock = new MutableTimeProvider(
                DateTimeOffset.FromUnixTimeMilliseconds(1_786_114_000_000));
            var aliceConversation = await aliceStore.GetOrCreateConversationAsync(bobIdentity.PeerId.Value);
            var outgoing = await aliceStore.QueueOutgoingTextAsync(
                aliceConversation.ConversationId,
                aliceIdentity.PeerId.Value,
                bobIdentity.PeerId.Value,
                "message survives a lost ACK",
                clock.GetUtcNow());

            var loopback = new LoopbackSender(
                bobIdentity.PeerId,
                async frame =>
                {
                    var bobResult = await bobProcessor.ProcessIncomingAsync(
                        frame,
                        bobIdentity.PeerId,
                        aliceIdentity.PeerId);
                    if (bobResult.ResponseFrame is null)
                    {
                        throw new InvalidOperationException("Bob did not produce a delivery ACK.");
                    }

                    return bobResult.ResponseFrame;
                },
                async ack =>
                {
                    await aliceProcessor.ProcessIncomingAsync(
                        ack,
                        aliceIdentity.PeerId,
                        bobIdentity.PeerId);
                })
            {
                DropNextAck = true
            };
            var worker = new OutboxDeliveryWorker(aliceOutbox, loopback, clock);

            var firstCycle = await worker.ProcessDueAsync(aliceIdentity.PeerId);

            Assert.Equal(new OutboxDeliveryCycleResult(1, 1, 0, 0), firstCycle);
            Assert.Single(await aliceStore.GetPendingOutboxAsync());
            Assert.Equal(1, loopback.SendCount);

            var bobConversation = await bobStore.GetOrCreateConversationAsync(aliceIdentity.PeerId.Value);
            var bobMessagesAfterFirstSend = await bobStore.GetMessagesAsync(bobConversation.ConversationId);
            var received = Assert.Single(bobMessagesAfterFirstSend);
            Assert.Equal(outgoing.MessageId, received.MessageId);
            Assert.Equal("message survives a lost ACK", received.Text);

            var pending = Assert.Single(await aliceStore.GetPendingOutboxAsync());
            clock.SetUtcNow(pending.NextAttemptAt);

            var secondCycle = await worker.ProcessDueAsync(aliceIdentity.PeerId);

            Assert.Equal(new OutboxDeliveryCycleResult(1, 0, 0, 1), secondCycle);
            Assert.Equal(2, loopback.SendCount);
            Assert.Empty(await aliceStore.GetPendingOutboxAsync());
            Assert.Single(await bobStore.GetMessagesAsync(bobConversation.ConversationId));

            var aliceMessages = await aliceStore.GetMessagesAsync(aliceConversation.ConversationId);
            var delivered = Assert.Single(aliceMessages);
            Assert.Equal(LocalMessageState.Delivered, delivered.State);
            Assert.NotNull(delivered.DeliveredAt);
        }
        finally
        {
            DeleteDatabaseFiles(aliceDatabase);
            DeleteDatabaseFiles(bobDatabase);
        }
    }

    private static string CreateDatabasePath(string name)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "dyract-e2e-reliability-tests",
            $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "local.db3");
    }

    private static void DeleteDatabaseFiles(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FixedKeyProvider(byte fill) : ILocalEncryptionKeyProvider
    {
        public ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Enumerable.Repeat(fill, 32).Select(value => (byte)value).ToArray());
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void SetUtcNow(DateTimeOffset value) => _now = value;
    }

    private sealed class LoopbackSender : IPeerApplicationFrameSender
    {
        private readonly PeerId _expectedRecipient;
        private readonly Func<ReadOnlyMemory<byte>, Task<byte[]>> _receiver;
        private readonly Func<ReadOnlyMemory<byte>, Task> _ackReceiver;

        public LoopbackSender(
            PeerId expectedRecipient,
            Func<ReadOnlyMemory<byte>, Task<byte[]>> receiver,
            Func<ReadOnlyMemory<byte>, Task> ackReceiver)
        {
            _expectedRecipient = expectedRecipient;
            _receiver = receiver;
            _ackReceiver = ackReceiver;
        }

        public bool DropNextAck { get; set; }
        public int SendCount { get; private set; }

        public async Task SendAsync(
            PeerId recipientPeerId,
            ReadOnlyMemory<byte> frame,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(_expectedRecipient, recipientPeerId);
            SendCount++;
            var ack = await _receiver(frame);
            if (DropNextAck)
            {
                DropNextAck = false;
                return;
            }

            await _ackReceiver(ack);
        }
    }
}
