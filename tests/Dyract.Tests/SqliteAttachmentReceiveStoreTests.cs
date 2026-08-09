using System.Security.Cryptography;
using System.Text;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Dyract.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dyract.Tests;

public sealed class SqliteAttachmentReceiveStoreTests
{
    [Fact]
    public async Task ReceiveState_SurvivesRestartAndResumesMissingChunks()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x63);
            using var sender = PeerIdentity.Generate();
            var data = RandomNumberGenerator.GetBytes(AttachmentProtocol.ChunkSizeBytes + 13);
            var manifest = AttachmentProtocol.CreateManifest(
                "restart-private-name.bin",
                "application/octet-stream",
                data.Length,
                SHA256.HashData(data));

            var firstLocalStore = new MigratingLocalStore(databasePath, keyProvider);
            var firstReceiveStore = new SqliteAttachmentReceiveStore(databasePath, keyProvider, firstLocalStore);
            Assert.Equal(
                AttachmentManifestStoreResult.Created,
                await firstReceiveStore.StoreManifestAsync(sender.PeerId.Value, manifest));

            var firstChunk = AttachmentProtocol.CreateChunk(
                manifest,
                0,
                data.AsSpan(0, AttachmentProtocol.ChunkSizeBytes));
            Assert.Equal(
                AttachmentChunkStoreResult.Stored,
                await firstReceiveStore.StoreChunkAsync(sender.PeerId.Value, firstChunk));

            var secondLocalStore = new MigratingLocalStore(databasePath, keyProvider);
            var secondReceiveStore = new SqliteAttachmentReceiveStore(databasePath, keyProvider, secondLocalStore);
            var restoredManifest = await secondReceiveStore.GetManifestAsync(sender.PeerId.Value, manifest.AttachmentId);
            Assert.Equal(manifest, restoredManifest);

            var missing = await secondReceiveStore.GetMissingRangesAsync(sender.PeerId.Value, manifest.AttachmentId);
            Assert.Equal(new[] { new AttachmentChunkRange(1, 1) }, missing);

            var restoredChunk = await secondReceiveStore.ReadChunkAsync(sender.PeerId.Value, manifest.AttachmentId, 0);
            Assert.NotNull(restoredChunk);
            Assert.Equal(firstChunk.Data, restoredChunk);
            CryptographicOperations.ZeroMemory(restoredChunk);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task DuplicateManifestAndChunk_AreIdempotentButChangedContentFailsClosed()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x64);
            using var sender = PeerIdentity.Generate();
            var localStore = new MigratingLocalStore(databasePath, keyProvider);
            var receiveStore = new SqliteAttachmentReceiveStore(databasePath, keyProvider, localStore);
            var data = RandomNumberGenerator.GetBytes(19);
            var manifest = AttachmentProtocol.CreateManifest(
                "duplicate.bin",
                null,
                data.Length,
                SHA256.HashData(data));

            Assert.Equal(
                AttachmentManifestStoreResult.Created,
                await receiveStore.StoreManifestAsync(sender.PeerId.Value, manifest));
            Assert.Equal(
                AttachmentManifestStoreResult.Existing,
                await receiveStore.StoreManifestAsync(sender.PeerId.Value, manifest));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                receiveStore.StoreManifestAsync(
                    sender.PeerId.Value,
                    manifest with { FileName = "changed.bin" }));

            var chunk = AttachmentProtocol.CreateChunk(manifest, 0, data);
            Assert.Equal(
                AttachmentChunkStoreResult.Stored,
                await receiveStore.StoreChunkAsync(sender.PeerId.Value, chunk));
            Assert.Equal(
                AttachmentChunkStoreResult.Duplicate,
                await receiveStore.StoreChunkAsync(sender.PeerId.Value, chunk));

            var changedData = data.ToArray();
            changedData[^1] ^= 0x40;
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                receiveStore.StoreChunkAsync(
                    sender.PeerId.Value,
                    chunk with { Data = changedData }));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task ReceiveState_IsPeerScopedAndSensitiveFieldsAreEncryptedAtRest()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x65);
            using var sender = PeerIdentity.Generate();
            using var otherPeer = PeerIdentity.Generate();
            var localStore = new MigratingLocalStore(databasePath, keyProvider);
            var receiveStore = new SqliteAttachmentReceiveStore(databasePath, keyProvider, localStore);
            var chunkBytes = Encoding.UTF8.GetBytes("PRIVATE-ATTACHMENT-CHUNK-SENTINEL-4fd2a811");
            var manifest = AttachmentProtocol.CreateManifest(
                "PRIVATE-FILENAME-SENTINEL-32ac.bin",
                "application/octet-stream",
                chunkBytes.Length,
                SHA256.HashData(chunkBytes));
            var chunk = AttachmentProtocol.CreateChunk(manifest, 0, chunkBytes);

            await receiveStore.StoreManifestAsync(sender.PeerId.Value, manifest);
            await receiveStore.StoreChunkAsync(sender.PeerId.Value, chunk);

            Assert.Null(await receiveStore.GetManifestAsync(otherPeer.PeerId.Value, manifest.AttachmentId));
            Assert.Null(await receiveStore.ReadChunkAsync(otherPeer.PeerId.Value, manifest.AttachmentId, 0));

            await CheckpointAsync(databasePath);
            var databaseBytes = await File.ReadAllBytesAsync(databasePath);
            var databaseText = Encoding.UTF8.GetString(databaseBytes);
            Assert.DoesNotContain("PRIVATE-FILENAME-SENTINEL-32ac", databaseText, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE-ATTACHMENT-CHUNK-SENTINEL-4fd2a811", databaseText, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task Remove_DeletesManifestAndChunksTogether()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x66);
            using var sender = PeerIdentity.Generate();
            var localStore = new MigratingLocalStore(databasePath, keyProvider);
            var receiveStore = new SqliteAttachmentReceiveStore(databasePath, keyProvider, localStore);
            var data = RandomNumberGenerator.GetBytes(8);
            var manifest = AttachmentProtocol.CreateManifest("remove.bin", null, data.Length, SHA256.HashData(data));

            await receiveStore.StoreManifestAsync(sender.PeerId.Value, manifest);
            await receiveStore.StoreChunkAsync(
                sender.PeerId.Value,
                AttachmentProtocol.CreateChunk(manifest, 0, data));

            await receiveStore.RemoveAsync(sender.PeerId.Value, manifest.AttachmentId);

            Assert.Null(await receiveStore.GetManifestAsync(sender.PeerId.Value, manifest.AttachmentId));
            Assert.Null(await receiveStore.ReadChunkAsync(sender.PeerId.Value, manifest.AttachmentId, 0));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static async Task CheckpointAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync();
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dyract-attachment-tests", Guid.NewGuid().ToString("N"));
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
