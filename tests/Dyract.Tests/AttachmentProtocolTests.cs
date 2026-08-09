using System.Security.Cryptography;
using Dyract.Protocol;
using Xunit;

namespace Dyract.Tests;

public sealed class AttachmentProtocolTests
{
    [Fact]
    public void ManifestAndChunks_UseFixedBoundedGeometry()
    {
        var data = RandomNumberGenerator.GetBytes(AttachmentProtocol.ChunkSizeBytes + 17);
        var manifest = AttachmentProtocol.CreateManifest(
            "photo.jpg",
            "image/jpeg",
            data.Length,
            SHA256.HashData(data));

        Assert.Equal(AttachmentProtocol.CurrentVersion, manifest.Version);
        Assert.Equal(2, manifest.ChunkCount);
        Assert.Equal(AttachmentProtocol.ChunkSizeBytes, manifest.ChunkSize);

        var first = AttachmentProtocol.CreateChunk(
            manifest,
            0,
            data.AsSpan(0, AttachmentProtocol.ChunkSizeBytes));
        var second = AttachmentProtocol.CreateChunk(
            manifest,
            1,
            data.AsSpan(AttachmentProtocol.ChunkSizeBytes));

        AttachmentProtocol.ValidateChunk(manifest, first);
        AttachmentProtocol.ValidateChunk(manifest, second);
        Assert.Equal(0, first.Offset);
        Assert.Equal(AttachmentProtocol.ChunkSizeBytes, second.Offset);
        Assert.Equal(17, second.Data.Length);

        Assert.Throws<ArgumentException>(() =>
            AttachmentProtocol.CreateChunk(manifest, 1, data.AsSpan(0, 18)));
    }

    [Fact]
    public void MissingRanges_AreCoalescedForResumeRequests()
    {
        var size = AttachmentProtocol.ChunkSizeBytes * 6L;
        var manifest = AttachmentProtocol.CreateManifest(
            "archive.bin",
            null,
            size,
            new byte[SHA256.HashSizeInBytes]);

        var ranges = AttachmentProtocol.GetMissingRanges(manifest, new[] { 0, 2, 3, 5, 5 });

        Assert.Equal(2, ranges.Count);
        Assert.Equal(new AttachmentChunkRange(1, 1), ranges[0]);
        Assert.Equal(new AttachmentChunkRange(4, 1), ranges[1]);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("folder/file.txt")]
    [InlineData("folder\\file.txt")]
    [InlineData(" bad.txt")]
    [InlineData("bad.txt ")]
    [InlineData("bad?.txt")]
    public void Manifest_RejectsUnsafeRemoteFileNames(string fileName)
    {
        Assert.Throws<InvalidDataException>(() => AttachmentProtocol.CreateManifest(
            fileName,
            "application/octet-stream",
            1,
            new byte[SHA256.HashSizeInBytes]));
    }

    [Fact]
    public void Manifest_RejectsOversizeOrNonCanonicalMetadata()
    {
        Assert.Throws<InvalidDataException>(() => AttachmentProtocol.CreateManifest(
            "large.bin",
            "application/octet-stream",
            AttachmentProtocol.MaximumAttachmentBytes + 1,
            new byte[SHA256.HashSizeInBytes]));

        var valid = AttachmentProtocol.CreateManifest(
            "small.bin",
            "application/octet-stream",
            1,
            new byte[SHA256.HashSizeInBytes]);

        Assert.Throws<InvalidDataException>(() => AttachmentProtocol.ValidateManifest(
            valid with { AttachmentId = valid.AttachmentId.ToUpperInvariant() }));
        Assert.Throws<InvalidDataException>(() => AttachmentProtocol.ValidateManifest(
            valid with { ContentType = "application/octet-stream; charset=binary" }));
    }

    [Fact]
    public async Task VerifySha256_RequiresExactSizeAndDigest()
    {
        var data = RandomNumberGenerator.GetBytes(AttachmentProtocol.ChunkSizeBytes + 123);
        var manifest = AttachmentProtocol.CreateManifest(
            "payload.dat",
            "application/octet-stream",
            data.Length,
            SHA256.HashData(data));

        await using var exact = new MemoryStream(data, writable: false);
        Assert.True(await AttachmentProtocol.VerifySha256Async(exact, manifest));

        var tampered = data.ToArray();
        tampered[^1] ^= 0x80;
        await using var changed = new MemoryStream(tampered, writable: false);
        Assert.False(await AttachmentProtocol.VerifySha256Async(changed, manifest));

        await using var shortStream = new MemoryStream(data.AsSpan(0, data.Length - 1).ToArray(), writable: false);
        Assert.False(await AttachmentProtocol.VerifySha256Async(shortStream, manifest));
    }
}
