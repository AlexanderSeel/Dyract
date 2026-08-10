using System.Security.Cryptography;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Dyract.Storage;
using Xunit;

namespace Dyract.Tests;

public sealed class AttachmentCompletionResetTests
{
    [Fact]
    public async Task ResetUserData_RemovesCompletedAttachmentReceipts()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x75);
            using var sender = PeerIdentity.Generate();
            var data = "verified before reset"u8.ToArray();
            var manifest = AttachmentProtocol.CreateManifest(
                "reset-completed.bin",
                null,
                data.Length,
                SHA256.HashData(data));
            var localStore = new MigratingLocalStore(databasePath, keyProvider);
            var receiveStore = new SqliteAttachmentReceiveStore(databasePath, keyProvider, localStore);

            await receiveStore.StoreManifestAsync(sender.PeerId.Value, manifest);
            await receiveStore.StoreChunkAsync(
                sender.PeerId.Value,
                AttachmentProtocol.CreateChunk(manifest, 0, data));
            await using (var staging = new MemoryStream())
            {
                var verified = await receiveStore.WriteVerifiedStagingAsync(
                    sender.PeerId.Value,
                    manifest.AttachmentId,
                    staging);
                await receiveStore.MarkCompletedAsync(verified);
            }

            Assert.NotNull(await receiveStore.GetCompletionReceiptAsync(
                sender.PeerId.Value,
                manifest.AttachmentId));

            await SqliteLocalResetter.ResetUserDataAsync(databasePath);

            Assert.Null(await receiveStore.GetCompletionReceiptAsync(
                sender.PeerId.Value,
                manifest.AttachmentId));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dyract-attachment-reset-tests", Guid.NewGuid().ToString("N"));
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
