using System.Security.Cryptography;
using Dyract.Client;
using Dyract.Core.Identity;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Dyract.Storage;
using Dyract.Transport;
using Xunit;

namespace Dyract.Tests;

public sealed class AttachmentOutboxWorkerTests
{
    [Fact]
    public async Task ProcessDue_SendsCanonicalFramesAndSchedulesAckRetry()
    {
        using var senderIdentity = PeerIdentity.Generate();
        using var recipientIdentity = PeerIdentity.Generate();
        var content = Enumerable.Range(0, 90_000).Select(index => (byte)index).ToArray();
        var manifest = AttachmentProtocol.CreateManifest(
            "worker.bin",
            "application/octet-stream",
            content.Length,
            SHA256.HashData(content),
            attachmentId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var chunks = Enumerable.Range(0, manifest.ChunkCount)
            .Select(index =>
            {
                var offset = index * manifest.ChunkSize;
                var length = Math.Min(manifest.ChunkSize, content.Length - offset);
                return AttachmentProtocol.CreateChunk(manifest, index, content.AsSpan(offset, length));
            })
            .ToArray();

        var outbox = new FakeAttachmentSendStore(new DueAttachmentSend(
            senderIdentity.PeerId.Value,
            recipientIdentity.PeerId.Value,
            manifest,
            chunks,
            SendManifest: true,
            CompletionProbe: false,
            Attempts: 0));
        var frameSender = new CapturingFrameSender();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-10T06:00:00Z"));
        var worker = new AttachmentOutboxWorker(outbox, frameSender, time);

        var result = await worker.ProcessDueAsync(senderIdentity.PeerId, chunksPerTransfer: 8);

        Assert.Equal(new AttachmentOutboxCycleResult(1, 1 + chunks.Length, 0, 0), result);
        Assert.Equal(1 + chunks.Length, frameSender.Frames.Count);
        Assert.IsType<AttachmentManifestApplicationFrame>(
            AttachmentApplicationFrameProtocol.Decode(frameSender.Frames[0]));
        for (var index = 0; index < chunks.Length; index++)
        {
            var decoded = Assert.IsType<AttachmentChunkApplicationFrame>(
                AttachmentApplicationFrameProtocol.Decode(frameSender.Frames[index + 1]));
            Assert.Equal(index, decoded.Chunk.ChunkIndex);
        }

        Assert.Equal(1, outbox.SentAttempts);
        Assert.Equal(0, outbox.FailedAttempts);
        Assert.Equal(time.GetUtcNow().AddSeconds(10), outbox.NextAttemptAt);
    }

    [Fact]
    public async Task ProcessDue_FailureSchedulesBoundedFailureRetry()
    {
        using var senderIdentity = PeerIdentity.Generate();
        using var recipientIdentity = PeerIdentity.Generate();
        var content = "small attachment"u8.ToArray();
        var manifest = AttachmentProtocol.CreateManifest(
            "worker.bin",
            "application/octet-stream",
            content.Length,
            SHA256.HashData(content),
            attachmentId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var chunk = AttachmentProtocol.CreateChunk(manifest, 0, content);
        var outbox = new FakeAttachmentSendStore(new DueAttachmentSend(
            senderIdentity.PeerId.Value,
            recipientIdentity.PeerId.Value,
            manifest,
            [chunk],
            SendManifest: true,
            CompletionProbe: false,
            Attempts: 2));
        var frameSender = new CapturingFrameSender { ThrowOnSend = true };
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-10T06:00:00Z"));
        var worker = new AttachmentOutboxWorker(outbox, frameSender, time);

        var result = await worker.ProcessDueAsync(senderIdentity.PeerId);

        Assert.Equal(new AttachmentOutboxCycleResult(1, 0, 1, 0), result);
        Assert.Equal(0, outbox.SentAttempts);
        Assert.Equal(1, outbox.FailedAttempts);
        Assert.Equal("send:IOException", outbox.FailureCode);
        Assert.Equal(time.GetUtcNow().AddSeconds(8), outbox.NextAttemptAt);
    }

    private sealed class FakeAttachmentSendStore(DueAttachmentSend due) : IAttachmentSendStore
    {
        public int SentAttempts { get; private set; }
        public int FailedAttempts { get; private set; }
        public string? FailureCode { get; private set; }
        public DateTimeOffset? NextAttemptAt { get; private set; }

        public Task QueueAsync(
            string senderPeerId,
            string recipientPeerId,
            AttachmentManifest manifest,
            IAsyncEnumerable<AttachmentChunk> chunks,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DueAttachmentSend>> GetDueAsync(
            DateTimeOffset dueAtOrBefore,
            int transferLimit = 4,
            int chunksPerTransfer = 16,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DueAttachmentSend>>([due]);

        public Task<bool> RecordAttemptSentAsync(
            string senderPeerId,
            string recipientPeerId,
            string attachmentId,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken = default)
        {
            SentAttempts++;
            NextAttemptAt = nextAttemptAt;
            return Task.FromResult(true);
        }

        public Task<bool> RecordAttemptFailureAsync(
            string senderPeerId,
            string recipientPeerId,
            string attachmentId,
            string failureCode,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken = default)
        {
            FailedAttempts++;
            FailureCode = failureCode;
            NextAttemptAt = nextAttemptAt;
            return Task.FromResult(true);
        }

        public Task<bool> ApplyResumeAsync(
            string senderPeerId,
            string recipientPeerId,
            AttachmentResumeApplicationFrame resume,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> MarkCompletedAsync(
            string senderPeerId,
            string recipientPeerId,
            AttachmentCompletionAcknowledgement acknowledgement,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private sealed class CapturingFrameSender : IPeerApplicationFrameSender
    {
        public List<byte[]> Frames { get; } = [];
        public bool ThrowOnSend { get; init; }

        public Task SendAsync(
            PeerId recipientPeerId,
            ReadOnlyMemory<byte> frame,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnSend)
            {
                throw new IOException("simulated transport failure");
            }

            Frames.Add(frame.ToArray());
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
