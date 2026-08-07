using Dyract.Crypto.Identity;
using Dyract.Storage;
using Xunit;

namespace Dyract.Tests;

public sealed class SqliteIncomingMessageStoreTests
{
    private const string IncomingMessageId = "019c1a2b3c4d7e8f9123456789abcdef";

    [Fact]
    public async Task StoreIncomingText_InsertsEncryptedMessageAndCreatesConversation()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x41);
            var localStore = new SqliteLocalStore(databasePath, keyProvider);
            var reliabilityStore = new SqliteIncomingMessageStore(databasePath, keyProvider, localStore);
            await localStore.InitializeAsync();

            using var localIdentity = PeerIdentity.Generate();
            using var remoteIdentity = PeerIdentity.Generate();
            await localStore.UpsertContactAsync(new ContactDraft(
                remoteIdentity.PeerId.Value,
                remoteIdentity.ExportPublicKey(),
                "Remote"));

            var createdAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            var receivedAt = DateTimeOffset.UtcNow;
            var stored = await reliabilityStore.StoreIncomingTextAsync(
                IncomingMessageId,
                remoteIdentity.PeerId.Value,
                localIdentity.PeerId.Value,
                "incoming secret text",
                createdAt,
                receivedAt);

            Assert.True(stored.IsNew);
            Assert.Equal(LocalMessageDirection.Incoming, stored.Message.Direction);
            Assert.Equal(LocalMessageState.Delivered, stored.Message.State);
            Assert.Equal("incoming secret text", stored.Message.Text);

            var conversation = await localStore.GetOrCreateConversationAsync(remoteIdentity.PeerId.Value);
            var messages = await localStore.GetMessagesAsync(conversation.ConversationId);
            var message = Assert.Single(messages);
            Assert.Equal(IncomingMessageId, message.MessageId);
            Assert.Equal(remoteIdentity.PeerId.Value, message.SenderPeerId);
            Assert.Equal(localIdentity.PeerId.Value, message.RecipientPeerId);
            Assert.Equal("incoming secret text", message.Text);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task StoreIncomingText_ExactDuplicateIsIdempotent()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x42);
            var localStore = new SqliteLocalStore(databasePath, keyProvider);
            var reliabilityStore = new SqliteIncomingMessageStore(databasePath, keyProvider, localStore);
            await localStore.InitializeAsync();

            using var localIdentity = PeerIdentity.Generate();
            using var remoteIdentity = PeerIdentity.Generate();
            await localStore.UpsertContactAsync(new ContactDraft(
                remoteIdentity.PeerId.Value,
                remoteIdentity.ExportPublicKey(),
                "Remote"));

            var createdAt = DateTimeOffset.UtcNow.AddSeconds(-2);
            var first = await reliabilityStore.StoreIncomingTextAsync(
                IncomingMessageId,
                remoteIdentity.PeerId.Value,
                localIdentity.PeerId.Value,
                "same payload",
                createdAt,
                DateTimeOffset.UtcNow.AddSeconds(-1));
            var duplicate = await reliabilityStore.StoreIncomingTextAsync(
                IncomingMessageId,
                remoteIdentity.PeerId.Value,
                localIdentity.PeerId.Value,
                "same payload",
                createdAt,
                DateTimeOffset.UtcNow);

            Assert.True(first.IsNew);
            Assert.False(duplicate.IsNew);
            Assert.Equal(first.Message.ConversationId, duplicate.Message.ConversationId);

            var messages = await localStore.GetMessagesAsync(first.Message.ConversationId);
            Assert.Single(messages);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task StoreIncomingText_DuplicateIdWithChangedContentIsRejected()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x43);
            var localStore = new SqliteLocalStore(databasePath, keyProvider);
            var reliabilityStore = new SqliteIncomingMessageStore(databasePath, keyProvider, localStore);
            await localStore.InitializeAsync();

            using var localIdentity = PeerIdentity.Generate();
            using var remoteIdentity = PeerIdentity.Generate();
            await localStore.UpsertContactAsync(new ContactDraft(
                remoteIdentity.PeerId.Value,
                remoteIdentity.ExportPublicKey(),
                "Remote"));

            var createdAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await reliabilityStore.StoreIncomingTextAsync(
                IncomingMessageId,
                remoteIdentity.PeerId.Value,
                localIdentity.PeerId.Value,
                "original",
                createdAt,
                DateTimeOffset.UtcNow);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                reliabilityStore.StoreIncomingTextAsync(
                    IncomingMessageId,
                    remoteIdentity.PeerId.Value,
                    localIdentity.PeerId.Value,
                    "changed",
                    createdAt,
                    DateTimeOffset.UtcNow));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task StoreIncomingText_UnsavedSenderIsRejected()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x44);
            var localStore = new SqliteLocalStore(databasePath, keyProvider);
            var reliabilityStore = new SqliteIncomingMessageStore(databasePath, keyProvider, localStore);
            await localStore.InitializeAsync();

            using var localIdentity = PeerIdentity.Generate();
            using var remoteIdentity = PeerIdentity.Generate();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                reliabilityStore.StoreIncomingTextAsync(
                    IncomingMessageId,
                    remoteIdentity.PeerId.Value,
                    localIdentity.PeerId.Value,
                    "not from a saved contact",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task MarkOutgoingDelivered_RequiresExactPeerScopeAndIsIdempotent()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x45);
            var localStore = new SqliteLocalStore(databasePath, keyProvider);
            var reliabilityStore = new SqliteIncomingMessageStore(databasePath, keyProvider, localStore);
            await localStore.InitializeAsync();

            using var localIdentity = PeerIdentity.Generate();
            using var remoteIdentity = PeerIdentity.Generate();
            using var otherRemote = PeerIdentity.Generate();
            await localStore.UpsertContactAsync(new ContactDraft(
                remoteIdentity.PeerId.Value,
                remoteIdentity.ExportPublicKey(),
                "Remote"));

            var conversation = await localStore.GetOrCreateConversationAsync(remoteIdentity.PeerId.Value);
            var outgoing = await localStore.QueueOutgoingTextAsync(
                conversation.ConversationId,
                localIdentity.PeerId.Value,
                remoteIdentity.PeerId.Value,
                "reliable message");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                reliabilityStore.MarkOutgoingDeliveredAsync(
                    outgoing.MessageId,
                    localIdentity.PeerId.Value,
                    otherRemote.PeerId.Value,
                    DateTimeOffset.UtcNow));

            Assert.Single(await localStore.GetPendingOutboxAsync());

            var deliveredAt = DateTimeOffset.UtcNow;
            Assert.True(await reliabilityStore.MarkOutgoingDeliveredAsync(
                outgoing.MessageId,
                localIdentity.PeerId.Value,
                remoteIdentity.PeerId.Value,
                deliveredAt));
            Assert.Empty(await localStore.GetPendingOutboxAsync());

            Assert.True(await reliabilityStore.MarkOutgoingDeliveredAsync(
                outgoing.MessageId,
                localIdentity.PeerId.Value,
                remoteIdentity.PeerId.Value,
                deliveredAt.AddSeconds(1)));

            var messages = await localStore.GetMessagesAsync(conversation.ConversationId);
            var message = Assert.Single(messages);
            Assert.Equal(LocalMessageState.Delivered, message.State);
            Assert.Equal(deliveredAt.ToUnixTimeMilliseconds(), message.DeliveredAt?.ToUnixTimeMilliseconds());
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task MarkOutgoingDelivered_UnknownMessageReturnsFalse()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x46);
            var localStore = new SqliteLocalStore(databasePath, keyProvider);
            var reliabilityStore = new SqliteIncomingMessageStore(databasePath, keyProvider, localStore);
            await localStore.InitializeAsync();

            using var localIdentity = PeerIdentity.Generate();
            using var remoteIdentity = PeerIdentity.Generate();

            Assert.False(await reliabilityStore.MarkOutgoingDeliveredAsync(
                IncomingMessageId,
                localIdentity.PeerId.Value,
                remoteIdentity.PeerId.Value,
                DateTimeOffset.UtcNow));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dyract-reliability-tests", Guid.NewGuid().ToString("N"));
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
}
