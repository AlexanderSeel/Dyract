using System.Security.Cryptography;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Dyract.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dyract.Tests;

public sealed class SqliteAttachmentSendStoreTests
{
    [Fact]
    public async Task SendState_SurvivesRestartAndTracksResumeUntilVerifiedCompletion()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var sender = PeerIdentity.Generate();
            using var recipient = PeerIdentity.Generate();
            using var otherPeer = PeerIdentity.Generate();
            var content = CreateContent(AttachmentProtocol.ChunkSizeBytes * 2 + 37);
            var manifest = CreateManifest(content);

            var firstLocalStore = new MigratingLocalStore(databasePath, new FixedKeyProvider(0x61));
            var firstStore = new SqliteAttachmentSendStore(
                databasePath,
                new FixedKeyProvider(0x61),
                firstLocalStore);
            await firstStore.QueueAsync(
                sender.PeerId.Value,
                recipient.PeerId.Value,
                manifest,
                EnumerateChunks(manifest, content));

            Assert.False(await RawPayloadContainsAsync(databasePath, content.AsSpan(0, 64).ToArray()));

            var restartedLocalStore = new MigratingLocalStore(databasePath, new FixedKeyProvider(0x61));
            var restarted = new SqliteAttachmentSendStore(
                databasePath,
                new FixedKeyProvider(0x61),
                restartedLocalStore);

            var due = Assert.Single(await restarted.GetDueAsync(
                DateTimeOffset.UtcNow.AddMinutes(1),
                chunksPerTransfer: 8));
            Assert.True(due.SendManifest);
            Assert.False(due.CompletionProbe);
            Assert.Equal(manifest, due.Manifest);
            Assert.Equal(new[] { 0, 1, 2 }, due.Chunks.Select(chunk => chunk.ChunkIndex).ToArray());

            var missingMiddle = new AttachmentResumeApplicationFrame(
                AttachmentProtocol.CurrentVersion,
                manifest.AttachmentId,
                new[] { new AttachmentChunkRange(1, 1) });
            Assert.True(await restarted.ApplyResumeAsync(
                sender.PeerId.Value,
                recipient.PeerId.Value,
                missingMiddle,
                DateTimeOffset.UtcNow));

            due = Assert.Single(await restarted.GetDueAsync(
                DateTimeOffset.UtcNow.AddMinutes(1),
                chunksPerTransfer: 8));
            Assert.False(due.SendManifest);
            Assert.False(due.CompletionProbe);
            Assert.Single(due.Chunks);
            Assert.Equal(1, due.Chunks[0].ChunkIndex);

            var nothingMissing = new AttachmentResumeApplicationFrame(
                AttachmentProtocol.CurrentVersion,
                manifest.AttachmentId,
                Array.Empty<AttachmentChunkRange>());
            Assert.True(await restarted.ApplyResumeAsync(
                sender.PeerId.Value,
                recipient.PeerId.Value,
                nothingMissing,
                DateTimeOffset.UtcNow));

            due = Assert.Single(await restarted.GetDueAsync(DateTimeOffset.UtcNow.AddMinutes(1)));
            Assert.True(due.SendManifest);
            Assert.True(due.CompletionProbe);
            Assert.Empty(due.Chunks);

            var completion = new AttachmentCompletionAcknowledgement(
                AttachmentProtocol.CurrentVersion,
                manifest.AttachmentId,
                manifest.Sha256);
            Assert.False(await restarted.MarkCompletedAsync(
                sender.PeerId.Value,
                otherPeer.PeerId.Value,
                completion));
            Assert.True(await restarted.MarkCompletedAsync(
                sender.PeerId.Value,
                recipient.PeerId.Value,
                completion));
            Assert.Empty(await restarted.GetDueAsync(DateTimeOffset.UtcNow.AddMinutes(1)));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task Queue_RejectsSnapshotWhoseChunksDoNotMatchManifestHash()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var sender = PeerIdentity.Generate();
            using var recipient = PeerIdentity.Generate();
            var expectedContent = CreateContent(80_000);
            var actualContent = expectedContent.ToArray();
            actualContent[^1] ^= 0x5a;
            var manifest = CreateManifest(expectedContent);
            var localStore = new MigratingLocalStore(databasePath, new FixedKeyProvider(0x62));
            var store = new SqliteAttachmentSendStore(
                databasePath,
                new FixedKeyProvider(0x62),
                localStore);

            await Assert.ThrowsAsync<InvalidDataException>(() => store.QueueAsync(
                sender.PeerId.Value,
                recipient.PeerId.Value,
                manifest,
                EnumerateChunks(manifest, actualContent)));

            Assert.Empty(await store.GetDueAsync(DateTimeOffset.UtcNow.AddMinutes(1)));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static AttachmentManifest CreateManifest(byte[] content)
        => AttachmentProtocol.CreateManifest(
            "sender-snapshot.bin",
            "application/octet-stream",
            content.Length,
            SHA256.HashData(content),
            attachmentId: "0123456789abcdeffedcba9876543210");

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
        var content = new byte[length];
        for (var index = 0; index < content.Length; index++)
        {
            content[index] = (byte)(index * 31 + 7);
        }

        return content;
    }

    private static async Task<bool> RawPayloadContainsAsync(string databasePath, byte[] plaintextPrefix)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM attachment_send_chunks ORDER BY chunk_index LIMIT 1;";
        var payload = (byte[])(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException());
        return payload.AsSpan().IndexOf(plaintextPrefix) >= 0;
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
