using Dyract.Crypto.Identity;
using Dyract.Storage;
using Xunit;

namespace Dyract.Tests;

public sealed class MessagePresentationOrderingTests
{
    [Fact]
    public async Task GetMessages_UsesLocalReceiveTimeForIncomingClockSkew()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x5A);
            var store = new SqliteLocalStore(databasePath, keyProvider);
            await store.InitializeAsync();

            using var alice = PeerIdentity.Generate();
            using var bob = PeerIdentity.Generate();
            await store.UpsertContactAsync(new ContactDraft(
                bob.PeerId.Value,
                bob.ExportPublicKey(),
                "Bob"));

            var conversation = await store.GetOrCreateConversationAsync(bob.PeerId.Value);
            var localBase = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);

            await store.QueueOutgoingTextAsync(
                conversation.ConversationId,
                alice.PeerId.Value,
                bob.PeerId.Value,
                "local first",
                localBase);
            await store.QueueOutgoingTextAsync(
                conversation.ConversationId,
                alice.PeerId.Value,
                bob.PeerId.Value,
                "local second",
                localBase.AddMinutes(1));

            var incomingStore = new SqliteIncomingMessageStore(databasePath, keyProvider, store);
            await incomingStore.StoreIncomingTextAsync(
                "11111111111111111111111111111111",
                bob.PeerId.Value,
                alice.PeerId.Value,
                "remote with old clock",
                localBase.AddDays(-30),
                localBase.AddMinutes(2));

            var latest = await store.GetMessagesAsync(conversation.ConversationId, limit: 2);

            Assert.Collection(
                latest,
                message => Assert.Equal("local second", message.Text),
                message => Assert.Equal("remote with old clock", message.Text));
            Assert.Equal(localBase.AddDays(-30), latest[1].CreatedAt);
            Assert.Equal(localBase.AddMinutes(2), latest[1].DeliveredAt);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dyract-ordering", Guid.NewGuid().ToString("N"));
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
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Enumerable.Repeat(fill, 32).Select(value => (byte)value).ToArray());
        }
    }
}
