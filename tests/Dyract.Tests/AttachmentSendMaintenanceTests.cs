using System.Security.Cryptography;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Dyract.Storage;
using Xunit;

namespace Dyract.Tests;

public sealed class AttachmentSendMaintenanceTests
{
    [Fact]
    public async Task Cancel_IsExactScopeAndCascadesQueuedChunks()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x76);
            using var sender = PeerIdentity.Generate();
            using var recipient = PeerIdentity.Generate();
            using var otherRecipient = PeerIdentity.Generate();
            var content = CreateContent(AttachmentProtocol.ChunkSizeBytes + 5);
            var manifest = AttachmentProtocol.CreateManifest(
                "cancel.bin",
                null,
                content.Length,
                SHA256.HashData(content),
                attachmentId: "44444444444444444444444444444444");
            var localStore = new MigratingLocalStore(databasePath, keyProvider);
            var sendStore = new SqliteAttachmentSendStore(databasePath, keyProvider, localStore);
            var maintenance = new SqliteAttachmentSendMaintenance(databasePath, localStore);

            await sendStore.QueueAsync(
                sender.PeerId.Value,
                recipient.PeerId.Value,
                manifest,
                EnumerateChunks(manifest, content));

            Assert.False(await maintenance.CancelAsync(
                sender.PeerId.Value,
                otherRecipient.PeerId.Value,
                manifest.AttachmentId));
            Assert.Single(await sendStore.GetDueAsync(DateTimeOffset.UtcNow.AddMinutes(1)));

            Assert.True(await maintenance.CancelAsync(
                sender.PeerId.Value,
                recipient.PeerId.Value,
                manifest.AttachmentId));
            Assert.Empty(await sendStore.GetDueAsync(DateTimeOffset.UtcNow.AddMinutes(1)));
            Assert.False(await maintenance.CancelAsync(
                sender.PeerId.Value,
                recipient.PeerId.Value,
                manifest.AttachmentId));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static async IAsyncEnumerable<AttachmentChunk> EnumerateChunks(
        AttachmentManifest manifest,
        byte[] content)
    {
        for (var index = 0; index < manifest.ChunkCount; index++)
        {
            var offset = index * manifest.ChunkSize;
            var length = Math.Min(manifest.ChunkSize, content.Length - offset);
            yield return AttachmentProtocol.CreateChunk(
                manifest,
                index,
                content.AsSpan(offset, length));
            await Task.Yield();
        }
    }

    private static byte[] CreateContent(int length)
    {
        var result = new byte[length];
        for (var index = 0; index < length; index++)
        {
            result[index] = (byte)(index * 13 + 3);
        }

        return result;
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dyract-attachment-maintenance-tests", Guid.NewGuid().ToString("N"));
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
