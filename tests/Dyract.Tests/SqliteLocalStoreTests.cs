using System.Security.Cryptography;
using Dyract.Crypto.Identity;
using Dyract.Storage;
using Xunit;

namespace Dyract.Tests;

public sealed class SqliteLocalStoreTests
{
    [Fact]
    public async Task QueueOutgoingText_PersistsMessageAndOutboxTransactionally()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var store = new SqliteLocalStore(databasePath, new FixedKeyProvider(0x11));
            await store.InitializeAsync();

            using var sender = PeerIdentity.Generate();
            using var recipient = PeerIdentity.Generate();
            await store.UpsertContactAsync(new ContactDraft(
                recipient.PeerId.Value,
                recipient.ExportPublicKey(),
                "Bob"));

            var conversation = await store.GetOrCreateConversationAsync(recipient.PeerId.Value);
            var queued = await store.QueueOutgoingTextAsync(
                conversation.ConversationId,
                sender.PeerId.Value,
                recipient.PeerId.Value,
                "hello from the outbox");

            var messages = await store.GetMessagesAsync(conversation.ConversationId);
            var outbox = await store.GetPendingOutboxAsync();

            Assert.Single(messages);
            Assert.Equal("hello from the outbox", messages[0].Text);
            Assert.Equal(LocalMessageState.Queued, messages[0].State);
            Assert.Single(outbox);
            Assert.Equal(queued.MessageId, outbox[0].MessageId);
            Assert.Equal("hello from the outbox", outbox[0].Text);

            await store.MarkDeliveredAsync(queued.MessageId, DateTimeOffset.UtcNow);

            messages = await store.GetMessagesAsync(conversation.ConversationId);
            outbox = await store.GetPendingOutboxAsync();
            Assert.Equal(LocalMessageState.Delivered, messages[0].State);
            Assert.Empty(outbox);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task ContactContent_CannotBeReadWithDifferentEncryptionKey()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var identity = PeerIdentity.Generate();
            var writer = new SqliteLocalStore(databasePath, new FixedKeyProvider(0x22));
            await writer.InitializeAsync();
            await writer.UpsertContactAsync(new ContactDraft(
                identity.PeerId.Value,
                identity.ExportPublicKey(),
                "Private local nickname"));

            var wrongKeyReader = new SqliteLocalStore(databasePath, new FixedKeyProvider(0x33));
            await wrongKeyReader.InitializeAsync();

            await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
                () => wrongKeyReader.GetContactsAsync());
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dyract-tests", Guid.NewGuid().ToString("N"));
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
