using Dyract.Crypto.Identity;
using Dyract.Storage;
using Xunit;

namespace Dyract.Tests;

public sealed class SqliteLocalResetterTests
{
    [Fact]
    public async Task ResetUserData_RemovesIdentityBoundRowsButKeepsStoreUsableWithRotatedKey()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var sender = PeerIdentity.Generate();
            using var firstContact = PeerIdentity.Generate();
            var originalStore = new MigratingLocalStore(databasePath, new FixedKeyProvider(0x41));
            await originalStore.InitializeAsync();
            await originalStore.UpsertContactAsync(new ContactDraft(
                firstContact.PeerId.Value,
                firstContact.ExportPublicKey(),
                "Before reset"));

            var conversation = await originalStore.GetOrCreateConversationAsync(firstContact.PeerId.Value);
            await originalStore.QueueOutgoingTextAsync(
                conversation.ConversationId,
                sender.PeerId.Value,
                firstContact.PeerId.Value,
                "must disappear");

            Assert.Single(await originalStore.GetContactsAsync());
            Assert.Single(await originalStore.GetMessagesAsync(conversation.ConversationId));
            Assert.Single(await originalStore.GetPendingOutboxAsync());

            await SqliteLocalResetter.ResetUserDataAsync(databasePath);

            Assert.Empty(await originalStore.GetContactsAsync());
            Assert.Empty(await originalStore.GetPendingOutboxAsync());

            using var secondContact = PeerIdentity.Generate();
            var rotatedStore = new MigratingLocalStore(databasePath, new FixedKeyProvider(0x52));
            await rotatedStore.InitializeAsync();
            await rotatedStore.UpsertContactAsync(new ContactDraft(
                secondContact.PeerId.Value,
                secondContact.ExportPublicKey(),
                "After reset"));

            var contacts = await rotatedStore.GetContactsAsync();
            Assert.Single(contacts);
            Assert.Equal("After reset", contacts[0].DisplayName);
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
