using System.Security.Cryptography;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Dyract.Storage;
using Microsoft.Data.Sqlite;
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
            var firstKey = new FixedKeyProvider(0x41);
            var originalStore = new MigratingLocalStore(databasePath, firstKey);
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

            var receiveStore = new SqliteAttachmentReceiveStore(databasePath, firstKey, originalStore);
            var attachmentData = RandomNumberGenerator.GetBytes(19);
            var manifest = AttachmentProtocol.CreateManifest(
                "partial.bin",
                null,
                attachmentData.Length,
                SHA256.HashData(attachmentData));
            await receiveStore.StoreManifestAsync(sender.PeerId.Value, manifest);
            await receiveStore.StoreChunkAsync(
                sender.PeerId.Value,
                AttachmentProtocol.CreateChunk(manifest, 0, attachmentData));

            var sendData = RandomNumberGenerator.GetBytes(31);
            var sendManifest = AttachmentProtocol.CreateManifest(
                "outgoing.bin",
                null,
                sendData.Length,
                SHA256.HashData(sendData));
            var sendStore = new SqliteAttachmentSendStore(databasePath, firstKey, originalStore);
            await sendStore.QueueAsync(
                sender.PeerId.Value,
                firstContact.PeerId.Value,
                sendManifest,
                EnumerateSingleChunk(sendManifest, sendData));

            Assert.Single(await originalStore.GetContactsAsync());
            Assert.Single(await originalStore.GetMessagesAsync(conversation.ConversationId));
            Assert.Single(await originalStore.GetPendingOutboxAsync());
            Assert.NotNull(await receiveStore.GetManifestAsync(sender.PeerId.Value, manifest.AttachmentId));
            Assert.Single(await sendStore.GetDueAsync(DateTimeOffset.UtcNow.AddMinutes(1)));

            await SqliteLocalResetter.ResetUserDataAsync(databasePath);

            Assert.Empty(await originalStore.GetContactsAsync());
            Assert.Empty(await originalStore.GetPendingOutboxAsync());
            Assert.Null(await receiveStore.GetManifestAsync(sender.PeerId.Value, manifest.AttachmentId));
            Assert.Empty(await sendStore.GetDueAsync(DateTimeOffset.UtcNow.AddMinutes(1)));

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

    [Fact]
    public async Task ResetUserData_AllowsDatabaseThatPredatesAttachmentTables()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var legacyStore = new SqliteLocalStore(databasePath, new FixedKeyProvider(0x53));
            await legacyStore.InitializeAsync();

            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='attachment_receives';";
                Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            }

            await SqliteLocalResetter.ResetUserDataAsync(databasePath);

            Assert.Empty(await legacyStore.GetContactsAsync());
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static async IAsyncEnumerable<AttachmentChunk> EnumerateSingleChunk(
        AttachmentManifest manifest,
        byte[] data)
    {
        yield return AttachmentProtocol.CreateChunk(manifest, 0, data);
        await Task.Yield();
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
