using System.Security.Cryptography;
using Dyract.Protocol;
using Xunit;

namespace Dyract.Tests;

public sealed class AttachmentStreamSnapshotTests
{
    [Fact]
    public async Task InspectAndReplay_RoundTripCanonicalSnapshot()
    {
        var payload = RandomNumberGenerator.GetBytes(AttachmentProtocol.ChunkSizeBytes + 17);
        await using var inspect = new MemoryStream(payload, writable: false);
        var manifest = await AttachmentStreamSnapshot.InspectAsync(
            inspect,
            "picked.bin",
            "application/octet-stream");

        Assert.Equal(payload.Length, manifest.SizeBytes);
        Assert.Equal(2, manifest.ChunkCount);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            manifest.Sha256);

        await using var replay = new MemoryStream(payload, writable: false);
        var chunks = new List<AttachmentChunk>();
        await foreach (var chunk in AttachmentStreamSnapshot.ReadChunksAsync(replay, manifest))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(2, chunks.Count);
        Assert.Equal(0, chunks[0].ChunkIndex);
        Assert.Equal(AttachmentProtocol.ChunkSizeBytes, chunks[0].Data.Length);
        Assert.Equal(1, chunks[1].ChunkIndex);
        Assert.Equal(17, chunks[1].Data.Length);
        Assert.Equal(payload, chunks.SelectMany(chunk => chunk.Data).ToArray());
    }

    [Fact]
    public async Task Inspect_RejectsEmptyAndOversizedSources()
    {
        await using var empty = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AttachmentStreamSnapshot.InspectAsync(empty, "empty.bin", null));

        await using var oversized = new RepeatingStream(AttachmentProtocol.MaximumAttachmentBytes + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AttachmentStreamSnapshot.InspectAsync(oversized, "large.bin", null));
    }

    [Fact]
    public async Task Replay_RejectsSourceThatChangedAfterInspection()
    {
        var original = RandomNumberGenerator.GetBytes(2048);
        await using var inspect = new MemoryStream(original, writable: false);
        var manifest = await AttachmentStreamSnapshot.InspectAsync(inspect, "mutable.bin", null);

        await using var shortened = new MemoryStream(original[..^1], writable: false);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in AttachmentStreamSnapshot.ReadChunksAsync(shortened, manifest))
            {
            }
        });

        var grown = new byte[original.Length + 1];
        original.CopyTo(grown, 0);
        grown[^1] = 0x42;
        await using var extended = new MemoryStream(grown, writable: false);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in AttachmentStreamSnapshot.ReadChunksAsync(extended, manifest))
            {
            }
        });
    }

    private sealed class RepeatingStream(long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => length - _remaining;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, _remaining);
            Array.Fill(buffer, (byte)0x5a, offset, read);
            _remaining -= read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = (int)Math.Min(buffer.Length, _remaining);
            buffer.Span[..read].Fill(0x5a);
            _remaining -= read;
            return ValueTask.FromResult(read);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
