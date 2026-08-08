using Dyract.Client;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Dyract.Storage;
using Xunit;

namespace Dyract.Tests;

public sealed class ReadReceiptTests
{
    [Fact]
    public void ReadAck_RoundTripsAndCreateReadAckReversesPeerScope()
    {
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        var message = new PeerTextMessageFrame(
            "0123456789abcdef0123456789abcdef",
            alice.PeerId,
            bob.PeerId,
            DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000),
            "hello");
        var readAt = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_001_000);

        var ack = PeerMessagingProtocol.CreateReadAck(message, readAt);
        var encoded = PeerMessagingProtocol.Encode(ack);

        Assert.Equal(bob.PeerId, ack.SenderPeerId);
        Assert.Equal(alice.PeerId, ack.RecipientPeerId);
        Assert.True(PeerMessagingProtocol.TryDecode(encoded, out var decoded, out var error), error);
        Assert.Equal(ack, Assert.IsType<PeerReadAckFrame>(decoded));
    }

    [Fact]
    public async Task IncomingRead_IsDurableAndCreatesScopedAckOnlyAfterExplicitMarkRead()
    {
        await using var context = await TestStoreContext.CreateAsync();
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        var receivedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_001_000);
        var readAt = receivedAt.AddSeconds(5);
        var messageId = "11111111111111111111111111111111";

        var stored = await context.Incoming.StoreIncomingTextAsync(
            messageId,
            bob.PeerId.Value,
            alice.PeerId.Value,
            "incoming text",
            receivedAt.AddSeconds(-1),
            receivedAt);
        Assert.Equal(LocalMessageState.Delivered, stored.Message.State);

        var service = new PeerReadReceiptService(context.ReadStore);
        var encodedAck = await service.MarkReadAndCreateAckAsync(
            messageId,
            alice.PeerId,
            bob.PeerId,
            readAt);

        Assert.NotNull(encodedAck);
        Assert.True(PeerMessagingProtocol.TryDecode(encodedAck, out var decoded, out var error), error);
        var ack = Assert.IsType<PeerReadAckFrame>(decoded);
        Assert.Equal(messageId, ack.MessageId);
        Assert.Equal(alice.PeerId, ack.SenderPeerId);
        Assert.Equal(bob.PeerId, ack.RecipientPeerId);
        Assert.Equal(readAt, ack.ReadAt);

        var messages = await context.LocalStore.GetMessagesAsync(stored.Message.ConversationId);
        var current = Assert.Single(messages);
        Assert.Equal(LocalMessageState.Read, current.State);
        Assert.Equal(readAt, current.ReadAt);
    }

    [Fact]
    public async Task ReadAck_CanSupersedeLostDeliveryAckAndRemoveOutgoingOutbox()
    {
        await using var context = await TestStoreContext.CreateAsync();
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        await context.LocalStore.UpsertContactAsync(new ContactDraft(
            bob.PeerId.Value,
            bob.ExportPublicKey(),
            "Bob",
            Capability: null));
        var conversation = await context.LocalStore.GetOrCreateConversationAsync(bob.PeerId.Value);
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
        var outgoing = await context.LocalStore.QueueOutgoingTextAsync(
            conversation.ConversationId,
            alice.PeerId.Value,
            bob.PeerId.Value,
            "queued text",
            createdAt);
        Assert.Single(await context.LocalStore.GetPendingOutboxAsync());

        var readAt = createdAt.AddSeconds(10);
        var processor = new PeerMessageProcessor(
            context.Incoming,
            context.Incoming,
            context.ReadStore,
            new FixedTimeProvider(readAt));
        var readAck = PeerMessagingProtocol.Encode(new PeerReadAckFrame(
            outgoing.MessageId,
            bob.PeerId,
            alice.PeerId,
            readAt));

        var result = await processor.ProcessIncomingAsync(
            readAck,
            alice.PeerId,
            bob.PeerId);

        Assert.Equal(PeerMessageProcessingKind.ReadAcknowledged, result.Kind);
        Assert.Empty(await context.LocalStore.GetPendingOutboxAsync());
        var updated = Assert.Single(await context.LocalStore.GetMessagesAsync(conversation.ConversationId));
        Assert.Equal(LocalMessageState.Read, updated.State);
        Assert.Equal(readAt, updated.ReadAt);
        Assert.Equal(readAt, updated.DeliveredAt);
    }

    [Fact]
    public async Task ReadAck_FromWrongPeerCannotChangeOutgoingMessage()
    {
        await using var context = await TestStoreContext.CreateAsync();
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        using var mallory = PeerIdentity.Generate();
        await context.LocalStore.UpsertContactAsync(new ContactDraft(
            bob.PeerId.Value,
            bob.ExportPublicKey(),
            "Bob",
            Capability: null));
        var conversation = await context.LocalStore.GetOrCreateConversationAsync(bob.PeerId.Value);
        var outgoing = await context.LocalStore.QueueOutgoingTextAsync(
            conversation.ConversationId,
            alice.PeerId.Value,
            bob.PeerId.Value,
            "peer scoped",
            DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000));
        var readAt = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_010_000);

        var wrongPeerAck = PeerMessagingProtocol.Encode(new PeerReadAckFrame(
            outgoing.MessageId,
            mallory.PeerId,
            alice.PeerId,
            readAt));
        var processor = new PeerMessageProcessor(
            context.Incoming,
            context.Incoming,
            context.ReadStore,
            new FixedTimeProvider(readAt));

        var result = await processor.ProcessIncomingAsync(
            wrongPeerAck,
            alice.PeerId,
            mallory.PeerId);

        Assert.Equal(PeerMessageProcessingKind.ReadAckUnknown, result.Kind);
        Assert.Single(await context.LocalStore.GetPendingOutboxAsync());
        var unchanged = Assert.Single(await context.LocalStore.GetMessagesAsync(conversation.ConversationId));
        Assert.Equal(LocalMessageState.Queued, unchanged.State);
        Assert.Null(unchanged.ReadAt);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedKeyProvider : ILocalEncryptionKeyProvider
    {
        private readonly byte[] _key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

        public ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_key.ToArray());
        }
    }

    private sealed class TestStoreContext : IAsyncDisposable
    {
        private readonly string _directory;

        private TestStoreContext(
            string directory,
            ILocalStore localStore,
            SqliteIncomingMessageStore incoming,
            SqliteReadReceiptStore readStore)
        {
            _directory = directory;
            LocalStore = localStore;
            Incoming = incoming;
            ReadStore = readStore;
        }

        public ILocalStore LocalStore { get; }
        public SqliteIncomingMessageStore Incoming { get; }
        public SqliteReadReceiptStore ReadStore { get; }

        public static async Task<TestStoreContext> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"dyract-read-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var databasePath = Path.Combine(directory, "local.db3");
            var keyProvider = new FixedKeyProvider();
            ILocalStore localStore = new MigratingLocalStore(databasePath, keyProvider);
            await localStore.InitializeAsync();
            var incoming = new SqliteIncomingMessageStore(databasePath, keyProvider, localStore);
            var readStore = new SqliteReadReceiptStore(databasePath, localStore);
            return new TestStoreContext(directory, localStore, incoming, readStore);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }
}
