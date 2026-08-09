using System.Security.Cryptography;
using Dyract.Protocol;
using Xunit;

namespace Dyract.Tests;

public sealed class AttachmentApplicationFrameProtocolTests
{
    [Fact]
    public void ManifestFrame_RoundTripsAndRejectsTrailingData()
    {
        var manifest = AttachmentProtocol.CreateManifest(
            "report.pdf",
            "application/pdf",
            12345,
            SHA256.HashData("manifest"u8));

        var encoded = AttachmentApplicationFrameProtocol.Encode(
            new AttachmentManifestApplicationFrame(manifest));
        var decoded = Assert.IsType<AttachmentManifestApplicationFrame>(
            AttachmentApplicationFrameProtocol.Decode(encoded));

        Assert.Equal(manifest, decoded.Manifest);

        var withTrailingByte = new byte[encoded.Length + 1];
        encoded.CopyTo(withTrailingByte, 0);
        Assert.Throws<InvalidDataException>(() =>
            AttachmentApplicationFrameProtocol.Decode(withTrailingByte));
    }

    [Fact]
    public void ChunkFrame_RoundTripsAndStillRequiresManifestGeometryValidation()
    {
        var data = RandomNumberGenerator.GetBytes(AttachmentProtocol.ChunkSizeBytes + 7);
        var manifest = AttachmentProtocol.CreateManifest(
            "blob.bin",
            null,
            data.Length,
            SHA256.HashData(data));
        var chunk = AttachmentProtocol.CreateChunk(
            manifest,
            1,
            data.AsSpan(AttachmentProtocol.ChunkSizeBytes));

        var encoded = AttachmentApplicationFrameProtocol.Encode(
            new AttachmentChunkApplicationFrame(chunk));
        var decoded = Assert.IsType<AttachmentChunkApplicationFrame>(
            AttachmentApplicationFrameProtocol.Decode(encoded));

        Assert.Equal(chunk.AttachmentId, decoded.Chunk.AttachmentId);
        Assert.Equal(chunk.ChunkIndex, decoded.Chunk.ChunkIndex);
        Assert.Equal(chunk.Offset, decoded.Chunk.Offset);
        Assert.Equal(chunk.Data, decoded.Chunk.Data);
        AttachmentProtocol.ValidateChunk(manifest, decoded.Chunk);

        var wrongGeometry = decoded.Chunk with { Offset = decoded.Chunk.Offset + 1 };
        var structurallyEncoded = AttachmentApplicationFrameProtocol.Encode(
            new AttachmentChunkApplicationFrame(wrongGeometry));
        var structurallyDecoded = Assert.IsType<AttachmentChunkApplicationFrame>(
            AttachmentApplicationFrameProtocol.Decode(structurallyEncoded));

        Assert.Throws<InvalidDataException>(() =>
            AttachmentProtocol.ValidateChunk(manifest, structurallyDecoded.Chunk));
    }

    [Fact]
    public void ResumeFrame_RoundTripsAndIsValidatedAgainstManifest()
    {
        var manifest = AttachmentProtocol.CreateManifest(
            "resume.bin",
            null,
            AttachmentProtocol.ChunkSizeBytes * 6L,
            new byte[SHA256.HashSizeInBytes]);
        var missing = AttachmentProtocol.GetMissingRanges(manifest, new[] { 0, 2, 3, 5 });
        var request = new AttachmentResumeApplicationFrame(
            AttachmentProtocol.CurrentVersion,
            manifest.AttachmentId,
            missing);

        var encoded = AttachmentApplicationFrameProtocol.Encode(request);
        var decoded = Assert.IsType<AttachmentResumeApplicationFrame>(
            AttachmentApplicationFrameProtocol.Decode(encoded));

        Assert.Equal(missing, decoded.MissingRanges);
        AttachmentApplicationFrameProtocol.ValidateResumeRequest(manifest, decoded);

        var otherManifest = manifest with { AttachmentId = "00112233445566778899aabbccddeeff" };
        Assert.Throws<InvalidDataException>(() =>
            AttachmentApplicationFrameProtocol.ValidateResumeRequest(otherManifest, decoded));
    }

    [Fact]
    public void ResumeFrame_RejectsOverlappingOrOverflowingRanges()
    {
        const string attachmentId = "00112233445566778899aabbccddeeff";

        Assert.Throws<InvalidDataException>(() =>
            AttachmentApplicationFrameProtocol.Encode(new AttachmentResumeApplicationFrame(
                AttachmentProtocol.CurrentVersion,
                attachmentId,
                new[]
                {
                    new AttachmentChunkRange(2, 2),
                    new AttachmentChunkRange(3, 1)
                })));

        Assert.Throws<InvalidDataException>(() =>
            AttachmentApplicationFrameProtocol.Encode(new AttachmentResumeApplicationFrame(
                AttachmentProtocol.CurrentVersion,
                attachmentId,
                new[] { new AttachmentChunkRange(int.MaxValue - 1, 10) })));
    }

    [Fact]
    public void Decoder_RejectsWrongMagicVersionTypeAndTruncation()
    {
        var manifest = AttachmentProtocol.CreateManifest(
            "sample.bin",
            null,
            32,
            new byte[SHA256.HashSizeInBytes]);
        var encoded = AttachmentApplicationFrameProtocol.Encode(
            new AttachmentManifestApplicationFrame(manifest));

        var wrongMagic = encoded.ToArray();
        wrongMagic[0] ^= 0x20;
        Assert.Throws<InvalidDataException>(() => AttachmentApplicationFrameProtocol.Decode(wrongMagic));

        var wrongVersion = encoded.ToArray();
        wrongVersion[4] = 99;
        Assert.Throws<InvalidDataException>(() => AttachmentApplicationFrameProtocol.Decode(wrongVersion));

        var wrongType = encoded.ToArray();
        wrongType[5] = 99;
        Assert.Throws<InvalidDataException>(() => AttachmentApplicationFrameProtocol.Decode(wrongType));

        for (var length = 0; length < encoded.Length; length++)
        {
            Assert.Throws<InvalidDataException>(() =>
                AttachmentApplicationFrameProtocol.Decode(encoded.AsSpan(0, length)));
        }
    }

    [Fact]
    public void MaximumChunkFrame_RemainsBelowAuthenticatedSessionRawLimit()
    {
        var manifest = AttachmentProtocol.CreateManifest(
            "max.bin",
            null,
            AttachmentProtocol.ChunkSizeBytes,
            new byte[SHA256.HashSizeInBytes]);
        var chunk = AttachmentProtocol.CreateChunk(
            manifest,
            0,
            new byte[AttachmentProtocol.ChunkSizeBytes]);

        var encoded = AttachmentApplicationFrameProtocol.Encode(
            new AttachmentChunkApplicationFrame(chunk));

        Assert.True(encoded.Length < 256 * 1024);
        Assert.True(encoded.Length <= AttachmentApplicationFrameProtocol.MaximumEncodedFrameBytes);
    }
}
