using System.Buffers.Binary;
using System.Security.Cryptography;
using Dyract.Protocol;
using Xunit;

namespace Dyract.Tests;

public sealed class AttachmentPreviewPolicyTests
{
    [Fact]
    public async Task PngPreview_RequiresExactManifestBytesAndExposesOnlyVerifiedSource()
    {
        var data = CreatePngHeader(640, 480);
        var manifest = CreateManifest(data, "photo.png", "image/png");

        await using var stream = new MemoryStream(data, writable: false);
        var result = await AttachmentPreviewPolicy.InspectCompletedAsync(stream, manifest);

        Assert.True(result.IsAllowed);
        Assert.Equal(AttachmentPreviewRejectionReason.None, result.RejectionReason);
        Assert.NotNull(result.Source);
        Assert.Equal(AttachmentRasterPreviewFormat.Png, result.Source.Format);
        Assert.Equal(640, result.Source.PixelWidth);
        Assert.Equal(480, result.Source.PixelHeight);
        Assert.Equal(data.Length, result.Source.Length);

        using (var verified = result.Source.OpenRead())
        using (var copy = new MemoryStream())
        {
            await verified.CopyToAsync(copy);
            Assert.Equal(data, copy.ToArray());
        }

        result.Source.Dispose();
        Assert.Throws<ObjectDisposedException>(() => result.Source.OpenRead());
    }

    [Fact]
    public async Task JpegPreview_ParsesBoundedStartOfFrameDimensions()
    {
        var data = CreateJpegHeader(1920, 1080);
        var manifest = CreateManifest(data, "photo.jpg", "image/jpeg");

        await using var stream = new MemoryStream(data, writable: false);
        var result = await AttachmentPreviewPolicy.InspectCompletedAsync(stream, manifest);

        Assert.True(result.IsAllowed);
        Assert.NotNull(result.Source);
        Assert.Equal(AttachmentRasterPreviewFormat.Jpeg, result.Source.Format);
        Assert.Equal(1920, result.Source.PixelWidth);
        Assert.Equal(1080, result.Source.PixelHeight);
        result.Source.Dispose();
    }

    [Fact]
    public async Task Preview_RejectsDeclaredTypeThatDoesNotMatchContentSignature()
    {
        var data = CreatePngHeader(32, 32);
        var manifest = CreateManifest(data, "wrong.jpg", "image/jpeg");

        await using var stream = new MemoryStream(data, writable: false);
        var result = await AttachmentPreviewPolicy.InspectCompletedAsync(stream, manifest);

        Assert.False(result.IsAllowed);
        Assert.Null(result.Source);
        Assert.Equal(
            AttachmentPreviewRejectionReason.ContentSignatureMismatch,
            result.RejectionReason);
    }

    [Fact]
    public async Task Preview_RejectsUnsupportedActiveOrComplexContentWithoutReadingIt()
    {
        var data = "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"u8.ToArray();
        var manifest = CreateManifest(data, "image.svg", "image/svg+xml");
        await using var stream = new ThrowOnReadStream(data.Length);

        var result = await AttachmentPreviewPolicy.InspectCompletedAsync(stream, manifest);

        Assert.False(result.IsAllowed);
        Assert.Equal(AttachmentPreviewRejectionReason.UnsupportedContentType, result.RejectionReason);
        Assert.Equal(0, stream.ReadCount);
    }

    [Fact]
    public async Task Preview_RejectsSourcesAboveAutomaticPreviewLimitWithoutReadingThem()
    {
        var manifest = AttachmentProtocol.CreateManifest(
            "large.jpg",
            "image/jpeg",
            AttachmentPreviewPolicy.MaximumRasterPreviewSourceBytes + 1L,
            new byte[SHA256.HashSizeInBytes]);
        await using var stream = new ThrowOnReadStream(manifest.SizeBytes);

        var result = await AttachmentPreviewPolicy.InspectCompletedAsync(stream, manifest);

        Assert.False(result.IsAllowed);
        Assert.Equal(AttachmentPreviewRejectionReason.SourceTooLarge, result.RejectionReason);
        Assert.Equal(0, stream.ReadCount);
    }

    [Fact]
    public async Task Preview_RejectsTamperedCompletedFileBeforeRasterAdmission()
    {
        var data = CreatePngHeader(64, 64);
        var manifest = CreateManifest(data, "photo.png", "image/png");
        var tampered = data.ToArray();
        tampered[^1] ^= 0x01;

        await using var stream = new MemoryStream(tampered, writable: false);
        var result = await AttachmentPreviewPolicy.InspectCompletedAsync(stream, manifest);

        Assert.False(result.IsAllowed);
        Assert.Equal(AttachmentPreviewRejectionReason.IntegrityMismatch, result.RejectionReason);
    }

    [Theory]
    [InlineData(8193, 10)]
    [InlineData(8000, 5000)]
    public async Task Preview_RejectsExcessiveRasterDimensionsOrPixelArea(int width, int height)
    {
        var data = CreatePngHeader(width, height);
        var manifest = CreateManifest(data, "oversized.png", "image/png");

        await using var stream = new MemoryStream(data, writable: false);
        var result = await AttachmentPreviewPolicy.InspectCompletedAsync(stream, manifest);

        Assert.False(result.IsAllowed);
        Assert.Equal(
            AttachmentPreviewRejectionReason.RasterDimensionsOutOfPolicy,
            result.RejectionReason);
    }

    [Fact]
    public async Task Preview_RejectsTruncatedSourceEvenWhenHeaderLooksEligible()
    {
        var data = CreatePngHeader(320, 240);
        var manifest = CreateManifest(data, "photo.png", "image/png");

        await using var stream = new MemoryStream(data.AsSpan(0, data.Length - 1).ToArray(), writable: false);
        var result = await AttachmentPreviewPolicy.InspectCompletedAsync(stream, manifest);

        Assert.False(result.IsAllowed);
        Assert.Equal(AttachmentPreviewRejectionReason.IntegrityMismatch, result.RejectionReason);
    }

    [Fact]
    public async Task Preview_RejectsTrailingGrowthBeyondManifestSnapshot()
    {
        var data = CreatePngHeader(320, 240);
        var manifest = CreateManifest(data, "photo.png", "image/png");
        var grown = data.Concat(new byte[] { 0x42 }).ToArray();

        await using var stream = new MemoryStream(grown, writable: false);
        var result = await AttachmentPreviewPolicy.InspectCompletedAsync(stream, manifest);

        Assert.False(result.IsAllowed);
        Assert.Equal(AttachmentPreviewRejectionReason.IntegrityMismatch, result.RejectionReason);
    }

    private static AttachmentManifest CreateManifest(byte[] data, string fileName, string contentType)
        => AttachmentProtocol.CreateManifest(
            fileName,
            contentType,
            data.Length,
            SHA256.HashData(data));

    private static byte[] CreatePngHeader(int width, int height)
    {
        var data = new byte[29];
        byte[] signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        signature.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(data.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20, 4), checked((uint)height));
        data[24] = 8;
        data[25] = 2;
        data[26] = 0;
        data[27] = 0;
        data[28] = 0;
        return data;
    }

    private static byte[] CreateJpegHeader(int width, int height)
    {
        var data = new byte[19];
        var offset = 0;
        data[offset++] = 0xff;
        data[offset++] = 0xd8;
        data[offset++] = 0xff;
        data[offset++] = 0xc0;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, 2), 11);
        offset += 2;
        data[offset++] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, 2), checked((ushort)height));
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, 2), checked((ushort)width));
        offset += 2;
        data[offset++] = 1;
        data[offset++] = 1;
        data[offset++] = 0x11;
        data[offset++] = 0;
        data[offset++] = 0xff;
        data[offset] = 0xd9;
        return data;
    }

    private sealed class ThrowOnReadStream(long length) : Stream
    {
        public int ReadCount { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            throw new InvalidOperationException("Preview policy should not read this stream.");
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            throw new InvalidOperationException("Preview policy should not read this stream.");
        }

        public override void Flush()
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }
}
