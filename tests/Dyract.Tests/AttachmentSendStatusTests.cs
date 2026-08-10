using System.Security.Cryptography;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Dyract.Storage;
using Xunit;

namespace Dyract.Tests;

public sealed class AttachmentSendStatusTests
{
    [Fact]
    public async Task StatusTracksFailureExplicitRetryProgressAndCancellation()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x37);
            var localStore = new MigratingLocalStore(databasePath, keyProvider);
            var sendStore = new SqliteAttachmentSendStore(databasePath, keyProvider, localStore);
            var statusStore = new SqliteAttachmentSendStatusStore(databasePath, keyProvider, localStore);
            var maintenance = new SqliteAttachmentSendMaintenance(databasePath, localStore);
            using var sender = PeerIdentity.Generate();
            using var recipient = PeerIdentity.Generate();

            var content = RandomNumberGenerator.GetBytes(AttachmentProtocol.ChunkSizeBytes + 9);
            var manifest = AttachmentProtocol.CreateManifest(
                "status.bin",
                "application/octet-stream",
                content.Length,
                SHA256.HashData(content),
                "55555555555555555555555555555555");
            await sendStore.QueueAsync(
                sender.PeerId.Value,
                recipient.PeerId.Value,
                manifest,
                EnumerateChunks(manifest, content));

            var initial = Assert.Single(await statusStore.GetPendingAsync(
                sender.PeerId.Value,
                recipient.PeerId.Value));
            Assert.Equal(manifest, initial.Manifest);
            Assert.Equal(0, initial.Attempts);
            Assert.Null(initial.LastFailure);
            Assert.Equal(2, initial.TotalChunks);
            Assert.Equal(0, initial.AcknowledgedChunks);
            Assert.Equal(2, initial.PendingChunks);
            Assert.False(initial.WaitingForCompletion);

            var retryAt = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
            Assert.True(await sendStore.RecordAttemptFailureAsync(
                sender.PeerId.Value,
                recipient.PeerId.Value,
                manifest.AttachmentId,
                "transport:closed",
                retryAt.AddMinutes(5)));

            var failed = Assert.Single(await statusStore.GetPendingAsync(
                sender.PeerId.Value,
                recipient.PeerId.Value));
            Assert.Equal(1, failed.Attempts);
            Assert.Equal("transport:closed", failed.LastFailure);
            Assert.Equal(retryAt.AddMinutes(5), failed.NextAttemptAt);

            Assert.True(await maintenance.RetryNowAsync(
                sender.PeerId.Value,
                recipient.PeerId.Value,
                manifest.AttachmentId,
                retryAt));
            var retried = Assert.Single(await statusStore.GetPendingAsync(
                sender.PeerId.Value,
                recipient.PeerId.Value));
            Assert.Equal(1, retried.Attempts);
            Assert.Null(retried.LastFailure);
            Assert.Equal(retryAt, retried.NextAttemptAt);

            Assert.True(await sendStore.ApplyResumeAsync(
                sender.PeerId.Value,
                recipient.PeerId.Value,
                new AttachmentResumeApplicationFrame(
                    AttachmentProtocol.CurrentVersion,
                    manifest.AttachmentId,
                    Array.Empty<AttachmentChunkRange>()),
                retryAt));
            var waiting = Assert.Single(await statusStore.GetPendingAsync(
                sender.PeerId.Value,
                recipient.PeerId.Value));
            Assert.Equal(2, waiting.AcknowledgedChunks);
            Assert.Equal(0, waiting.PendingChunks);
            Assert.True(waiting.WaitingForCompletion);

            Assert.True(await maintenance.CancelAsync(
                sender.PeerId.Value,
                recipient.PeerId.Value,
                manifest.AttachmentId));
            Assert.Empty(await statusStore.GetPendingAsync(
                sender.PeerId.Value,
                recipient.PeerId.Value));
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
            yield return AttachmentProtocol.CreateChunk(manifest, index, content.AsSpan(offset, length));
            await Task.Yield();
        }
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dyract-attachment-status-tests", Guid.NewGuid().ToString("N"));
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
