using System.Security.Cryptography;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Dyract.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dyract.Tests;

public sealed class AttachmentReceiveQuotaTests
{
    [Fact]
    public async Task FifthActiveReceiveFromSamePeer_IsRejectedByDatabaseQuota()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x71);
            using var sender = PeerIdentity.Generate();
            var localStore = new MigratingLocalStore(databasePath, keyProvider);
            var receiveStore = new SqliteAttachmentReceiveStore(databasePath, keyProvider, localStore);

            for (var index = 0; index < 4; index++)
            {
                var manifest = AttachmentProtocol.CreateManifest(
                    $"quota-{index}.bin",
                    null,
                    1,
                    SHA256.HashData(new[] { (byte)index }));
                Assert.Equal(
                    AttachmentManifestStoreResult.Created,
                    await receiveStore.StoreManifestAsync(sender.PeerId.Value, manifest));
            }

            var blocked = AttachmentProtocol.CreateManifest(
                "quota-blocked.bin",
                null,
                1,
                SHA256.HashData(new byte[] { 0xff }));
            var exception = await Assert.ThrowsAsync<SqliteException>(() =>
                receiveStore.StoreManifestAsync(sender.PeerId.Value, blocked));

            Assert.Contains("attachment_receive_quota", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dyract-attachment-quota-tests", Guid.NewGuid().ToString("N"));
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
