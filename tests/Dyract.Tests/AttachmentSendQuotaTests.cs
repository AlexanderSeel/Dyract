using System.Security.Cryptography;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Dyract.Storage;
using Xunit;

namespace Dyract.Tests;

public sealed class AttachmentSendQuotaTests
{
    [Fact]
    public async Task Queue_EnforcesPerRecipientActiveTransferLimitAtomically()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var sender = PeerIdentity.Generate();
            using var recipient = PeerIdentity.Generate();
            var keyProvider = new FixedKeyProvider(0x71);
            var localStore = new MigratingLocalStore(databasePath, keyProvider);
            var store = new SqliteAttachmentSendStore(databasePath, keyProvider, localStore);

            for (var index = 1; index <= 4; index++)
            {
                await QueueOneByteAsync(store, sender.PeerId.Value, recipient.PeerId.Value, index);
            }

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                QueueOneByteAsync(store, sender.PeerId.Value, recipient.PeerId.Value, 5));
            Assert.Contains("quota", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(4, (await store.GetDueAsync(DateTimeOffset.UtcNow.AddMinutes(1), transferLimit: 16)).Count);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static async Task QueueOneByteAsync(
        SqliteAttachmentSendStore store,
        string senderPeerId,
        string recipientPeerId,
        int id)
    {
        var data = new[] { (byte)id };
        var manifest = AttachmentProtocol.CreateManifest(
            $"file-{id}.bin",
            "application/octet-stream",
            data.Length,
            SHA256.HashData(data),
            attachmentId: id.ToString("x32"));
        await store.QueueAsync(
            senderPeerId,
            recipientPeerId,
            manifest,
            Enumerate(manifest, data));
    }

    private static async IAsyncEnumerable<AttachmentChunk> Enumerate(
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
