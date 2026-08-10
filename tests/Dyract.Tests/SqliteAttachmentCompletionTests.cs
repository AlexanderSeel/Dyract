using System.Security.Cryptography;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Dyract.Storage;
using Xunit;

namespace Dyract.Tests;

public sealed class SqliteAttachmentCompletionTests
{
    [Fact]
    public async Task VerifiedCompletion_SurvivesRestartAndReemitsLostFinalAck()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x71);
            var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-10T06:30:00Z"));
            using var sender = PeerIdentity.Generate();
            var content = CreateContent(AttachmentProtocol.ChunkSizeBytes + 91);
            var manifest = AttachmentProtocol.CreateManifest(
                "verified.bin",
                "application/octet-stream",
                content.Length,
                SHA256.HashData(content),
                attachmentId: "11111111111111111111111111111111");

            var localStore = new MigratingLocalStore(databasePath, keyProvider);
            var receiveStore = new SqliteAttachmentReceiveStore(databasePath, keyProvider, localStore, time);
            await StoreCompleteSnapshotAsync(receiveStore, sender.PeerId.Value, manifest, content);

            await using var staging = new MemoryStream();
            var verified = await receiveStore.WriteVerifiedStagingAsync(
                sender.PeerId.Value,
                manifest.AttachmentId,
                staging);
            Assert.Equal(content, staging.ToArray());

            // Represents the caller successfully promoting the verified staging file.
            var acknowledgement = await receiveStore.MarkCompletedAsync(verified);
            Assert.Equal(manifest.AttachmentId, acknowledgement.AttachmentId);
            Assert.Equal(manifest.Sha256, acknowledgement.Sha256);
            Assert.Null(await receiveStore.GetManifestAsync(sender.PeerId.Value, manifest.AttachmentId));
            Assert.Null(await receiveStore.ReadChunkAsync(sender.PeerId.Value, manifest.AttachmentId, 0));

            var restartedLocalStore = new MigratingLocalStore(databasePath, keyProvider);
            var restarted = new SqliteAttachmentReceiveStore(databasePath, keyProvider, restartedLocalStore, time);
            var receipt = await restarted.GetCompletionReceiptAsync(sender.PeerId.Value, manifest.AttachmentId);
            Assert.NotNull(receipt);
            Assert.Equal(acknowledgement, receipt.Acknowledgement);

            // The sender may replay its manifest as a completion probe if DYAC was lost.
            Assert.Equal(
                AttachmentManifestStoreResult.Completed,
                await restarted.StoreManifestAsync(sender.PeerId.Value, manifest));
            Assert.Equal(
                acknowledgement,
                (await restarted.GetCompletionReceiptAsync(sender.PeerId.Value, manifest.AttachmentId))!.Acknowledgement);

            await Assert.ThrowsAsync<InvalidDataException>(() => restarted.StoreManifestAsync(
                sender.PeerId.Value,
                manifest with { FileName = "collision.bin" }));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task Verification_RejectsMissingChunksBeforeWritingStaging()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x72);
            using var sender = PeerIdentity.Generate();
            var content = CreateContent(AttachmentProtocol.ChunkSizeBytes + 3);
            var manifest = AttachmentProtocol.CreateManifest(
                "missing.bin",
                null,
                content.Length,
                SHA256.HashData(content));
            var localStore = new MigratingLocalStore(databasePath, keyProvider);
            var receiveStore = new SqliteAttachmentReceiveStore(databasePath, keyProvider, localStore);
            await receiveStore.StoreManifestAsync(sender.PeerId.Value, manifest);
            await receiveStore.StoreChunkAsync(
                sender.PeerId.Value,
                AttachmentProtocol.CreateChunk(
                    manifest,
                    0,
                    content.AsSpan(0, AttachmentProtocol.ChunkSizeBytes)));

            await using var staging = new MemoryStream();
            await Assert.ThrowsAsync<InvalidDataException>(() => receiveStore.WriteVerifiedStagingAsync(
                sender.PeerId.Value,
                manifest.AttachmentId,
                staging));
            Assert.Equal(0, staging.Length);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task Cleanup_ExpiresCompletionReceiptsBeforeLongerLivedPartials()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x73);
            var start = DateTimeOffset.Parse("2026-08-10T06:30:00Z");
            var time = new MutableTimeProvider(start);
            using var sender = PeerIdentity.Generate();
            var localStore = new MigratingLocalStore(databasePath, keyProvider);
            var receiveStore = new SqliteAttachmentReceiveStore(databasePath, keyProvider, localStore, time);

            var completedContent = "completed"u8.ToArray();
            var completedManifest = AttachmentProtocol.CreateManifest(
                "completed.bin",
                null,
                completedContent.Length,
                SHA256.HashData(completedContent),
                attachmentId: "22222222222222222222222222222222");
            await StoreCompleteSnapshotAsync(receiveStore, sender.PeerId.Value, completedManifest, completedContent);
            await using (var staging = new MemoryStream())
            {
                var verified = await receiveStore.WriteVerifiedStagingAsync(
                    sender.PeerId.Value,
                    completedManifest.AttachmentId,
                    staging);
                await receiveStore.MarkCompletedAsync(verified);
            }

            var partialContent = "partial"u8.ToArray();
            var partialManifest = AttachmentProtocol.CreateManifest(
                "partial.bin",
                null,
                partialContent.Length,
                SHA256.HashData(partialContent),
                attachmentId: "33333333333333333333333333333333");
            await receiveStore.StoreManifestAsync(sender.PeerId.Value, partialManifest);

            time.Advance(TimeSpan.FromDays(8));
            var firstCleanup = await receiveStore.CleanupStaleAsync();
            Assert.Equal(new AttachmentReceiveCleanupResult(0, 1), firstCleanup);
            Assert.Null(await receiveStore.GetCompletionReceiptAsync(sender.PeerId.Value, completedManifest.AttachmentId));
            Assert.NotNull(await receiveStore.GetManifestAsync(sender.PeerId.Value, partialManifest.AttachmentId));

            time.Advance(TimeSpan.FromDays(7));
            var secondCleanup = await receiveStore.CleanupStaleAsync();
            Assert.Equal(1, secondCleanup.PartialReceivesRemoved);
            Assert.Null(await receiveStore.GetManifestAsync(sender.PeerId.Value, partialManifest.AttachmentId));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task CompletionReceipts_AreBoundedPerSender()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x74);
            var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-10T06:30:00Z"));
            using var sender = PeerIdentity.Generate();
            var localStore = new MigratingLocalStore(databasePath, keyProvider);
            var receiveStore = new SqliteAttachmentReceiveStore(databasePath, keyProvider, localStore, time);

            for (var index = 0; index < 65; index++)
            {
                var content = new[] { (byte)index };
                var attachmentId = index.ToString("x32");
                var manifest = AttachmentProtocol.CreateManifest(
                    $"bounded-{index}.bin",
                    null,
                    content.Length,
                    SHA256.HashData(content),
                    attachmentId);
                await StoreCompleteSnapshotAsync(receiveStore, sender.PeerId.Value, manifest, content);
                await using var staging = new MemoryStream();
                var verified = await receiveStore.WriteVerifiedStagingAsync(
                    sender.PeerId.Value,
                    manifest.AttachmentId,
                    staging);
                await receiveStore.MarkCompletedAsync(verified);
                time.Advance(TimeSpan.FromMilliseconds(1));
            }

            Assert.Null(await receiveStore.GetCompletionReceiptAsync(
                sender.PeerId.Value,
                0.ToString("x32")));
            Assert.NotNull(await receiveStore.GetCompletionReceiptAsync(
                sender.PeerId.Value,
                64.ToString("x32")));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static async Task StoreCompleteSnapshotAsync(
        SqliteAttachmentReceiveStore store,
        string senderPeerId,
        AttachmentManifest manifest,
        byte[] content)
    {
        Assert.Equal(
            AttachmentManifestStoreResult.Created,
            await store.StoreManifestAsync(senderPeerId, manifest));
        for (var index = 0; index < manifest.ChunkCount; index++)
        {
            var offset = index * manifest.ChunkSize;
            var length = Math.Min(manifest.ChunkSize, content.Length - offset);
            await store.StoreChunkAsync(
                senderPeerId,
                AttachmentProtocol.CreateChunk(manifest, index, content.AsSpan(offset, length)));
        }
    }

    private static byte[] CreateContent(int length)
    {
        var result = new byte[length];
        for (var index = 0; index < length; index++)
        {
            result[index] = (byte)(index * 17 + 11);
        }

        return result;
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dyract-attachment-completion-tests", Guid.NewGuid().ToString("N"));
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

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }
}
