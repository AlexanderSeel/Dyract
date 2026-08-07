using Dyract.Crypto.Identity;
using Dyract.Storage;
using Xunit;

namespace Dyract.Tests;

public sealed class SqliteOutboxDeliveryQueueTests
{
    [Fact]
    public async Task DueQueue_PreservesOriginalMessageAndTracksAckRetryAttempts()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x51);
            var localStore = new SqliteLocalStore(databasePath, keyProvider);
            var queue = new SqliteOutboxDeliveryQueue(databasePath, keyProvider, localStore);
            await localStore.InitializeAsync();

            using var local = PeerIdentity.Generate();
            using var remote = PeerIdentity.Generate();
            await localStore.UpsertContactAsync(new ContactDraft(
                remote.PeerId.Value,
                remote.ExportPublicKey(),
                "Remote"));
            var conversation = await localStore.GetOrCreateConversationAsync(remote.PeerId.Value);
            var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(1_786_113_000_000);
            var outgoing = await localStore.QueueOutgoingTextAsync(
                conversation.ConversationId,
                local.PeerId.Value,
                remote.PeerId.Value,
                "durable retry payload",
                createdAt);

            Assert.Empty(await queue.GetDueOutboxAsync(createdAt.AddMilliseconds(-1)));
            var due = Assert.Single(await queue.GetDueOutboxAsync(createdAt));
            Assert.Equal(outgoing.MessageId, due.MessageId);
            Assert.Equal(local.PeerId.Value, due.SenderPeerId);
            Assert.Equal(remote.PeerId.Value, due.RecipientPeerId);
            Assert.Equal(createdAt, due.CreatedAt);
            Assert.Equal("durable retry payload", due.Text);
            Assert.Equal(0, due.Attempts);

            var nextAttemptAt = createdAt.AddSeconds(10);
            Assert.True(await queue.RecordOutboundSentAsync(
                outgoing.MessageId,
                local.PeerId.Value,
                remote.PeerId.Value,
                nextAttemptAt));

            Assert.Empty(await queue.GetDueOutboxAsync(nextAttemptAt.AddMilliseconds(-1)));
            var retry = Assert.Single(await queue.GetDueOutboxAsync(nextAttemptAt));
            Assert.Equal(1, retry.Attempts);
            Assert.Equal(createdAt, retry.CreatedAt);
            Assert.Equal("durable retry payload", retry.Text);

            var pending = Assert.Single(await localStore.GetPendingOutboxAsync());
            Assert.Equal(1, pending.Attempts);
            Assert.Equal(nextAttemptAt, pending.NextAttemptAt);
            var stored = Assert.Single(await localStore.GetMessagesAsync(conversation.ConversationId));
            Assert.Equal(LocalMessageState.Sent, stored.State);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task FailedAttempt_IsRetryableAndPeerScoped()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x52);
            var localStore = new SqliteLocalStore(databasePath, keyProvider);
            var queue = new SqliteOutboxDeliveryQueue(databasePath, keyProvider, localStore);
            await localStore.InitializeAsync();

            using var local = PeerIdentity.Generate();
            using var remote = PeerIdentity.Generate();
            using var wrongRemote = PeerIdentity.Generate();
            await localStore.UpsertContactAsync(new ContactDraft(
                remote.PeerId.Value,
                remote.ExportPublicKey(),
                "Remote"));
            var conversation = await localStore.GetOrCreateConversationAsync(remote.PeerId.Value);
            var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(1_786_113_100_000);
            var outgoing = await localStore.QueueOutgoingTextAsync(
                conversation.ConversationId,
                local.PeerId.Value,
                remote.PeerId.Value,
                "retry after failure",
                createdAt);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                queue.RecordOutboundFailureAsync(
                    outgoing.MessageId,
                    local.PeerId.Value,
                    wrongRemote.PeerId.Value,
                    "send:TimeoutException",
                    createdAt.AddSeconds(2)));

            var pendingBefore = Assert.Single(await localStore.GetPendingOutboxAsync());
            Assert.Equal(0, pendingBefore.Attempts);

            var nextAttemptAt = createdAt.AddSeconds(2);
            Assert.True(await queue.RecordOutboundFailureAsync(
                outgoing.MessageId,
                local.PeerId.Value,
                remote.PeerId.Value,
                "send:TimeoutException",
                nextAttemptAt));

            var due = Assert.Single(await queue.GetDueOutboxAsync(nextAttemptAt));
            Assert.Equal(1, due.Attempts);
            var stored = Assert.Single(await localStore.GetMessagesAsync(conversation.ConversationId));
            Assert.Equal(LocalMessageState.Failed, stored.State);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task DeliveredAck_RemovesMessageFromDueQueue()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x53);
            var localStore = new SqliteLocalStore(databasePath, keyProvider);
            var reliability = new SqliteIncomingMessageStore(databasePath, keyProvider, localStore);
            var queue = new SqliteOutboxDeliveryQueue(databasePath, keyProvider, localStore);
            await localStore.InitializeAsync();

            using var local = PeerIdentity.Generate();
            using var remote = PeerIdentity.Generate();
            await localStore.UpsertContactAsync(new ContactDraft(
                remote.PeerId.Value,
                remote.ExportPublicKey(),
                "Remote"));
            var conversation = await localStore.GetOrCreateConversationAsync(remote.PeerId.Value);
            var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(1_786_113_200_000);
            var outgoing = await localStore.QueueOutgoingTextAsync(
                conversation.ConversationId,
                local.PeerId.Value,
                remote.PeerId.Value,
                "acked payload",
                createdAt);

            Assert.Single(await queue.GetDueOutboxAsync(createdAt));
            Assert.True(await reliability.MarkOutgoingDeliveredAsync(
                outgoing.MessageId,
                local.PeerId.Value,
                remote.PeerId.Value,
                createdAt.AddSeconds(1)));

            Assert.Empty(await queue.GetDueOutboxAsync(createdAt.AddDays(1)));
            Assert.Empty(await localStore.GetPendingOutboxAsync());
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dyract-outbox-tests", Guid.NewGuid().ToString("N"));
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
