using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Dyract.Protocol;

public enum AttachmentRasterPreviewFormat
{
    Png = 1,
    Jpeg = 2
}

public enum AttachmentPreviewRejectionReason
{
    None = 0,
    UnsupportedContentType,
    SourceTooLarge,
    IntegrityMismatch,
    ContentSignatureMismatch,
    InvalidRasterHeader,
    RasterDimensionsOutOfPolicy
}

/// <summary>
/// Holds one bounded, integrity-verified raster preview source. Instances can only be created by
/// <see cref="AttachmentPreviewPolicy"/> after the complete source matches its attachment manifest.
/// Disposing the instance clears its retained source bytes.
/// </summary>
public sealed class VerifiedAttachmentPreviewSource : IDisposable
{
    private byte[]? _content;

    internal VerifiedAttachmentPreviewSource(
        byte[] content,
        AttachmentRasterPreviewFormat format,
        int pixelWidth,
        int pixelHeight)
    {
        _content = content;
        Format = format;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }

    public AttachmentRasterPreviewFormat Format { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public int Length
        => _content?.Length ?? throw new ObjectDisposedException(nameof(VerifiedAttachmentPreviewSource));

    /// <summary>
    /// Opens a read-only stream over the exact bytes that passed manifest hash and raster admission checks.
    /// A future platform thumbnail decoder must consume this stream rather than reopening an unverified path.
    /// </summary>
    public Stream OpenRead()
    {
        var content = _content ?? throw new ObjectDisposedException(nameof(VerifiedAttachmentPreviewSource));
        return new MemoryStream(content, 0, content.Length, writable: false, publiclyVisible: false);
    }

    public void Dispose()
    {
        var content = Interlocked.Exchange(ref _content, null);
        if (content is not null)
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }
}

public sealed record AttachmentPreviewAdmissionResult(
    VerifiedAttachmentPreviewSource? Source,
    AttachmentPreviewRejectionReason RejectionReason)
{
    public bool IsAllowed => Source is not null;

    internal static AttachmentPreviewAdmissionResult Allowed(VerifiedAttachmentPreviewSource source)
        => new(source, AttachmentPreviewRejectionReason.None);

    internal static AttachmentPreviewAdmissionResult Rejected(AttachmentPreviewRejectionReason reason)
        => new(null, reason);
}

/// <summary>
/// Defines the mandatory pre-decode boundary for automatic attachment thumbnails/previews.
/// It does not decode image pixels. It only admits a deliberately narrow raster subset after
/// exact manifest integrity verification, conservative source-size limits, content-signature checks,
/// and bounded dimensions/pixel count.
/// </summary>
public static class AttachmentPreviewPolicy
{
    public const int MaximumRasterPreviewSourceBytes = 8 * 1024 * 1024;
    public const int MaximumRasterDimension = 8192;
    public const long MaximumRasterPixels = 32_000_000;

    private const string PngContentType = "image/png";
    private const string JpegContentType = "image/jpeg";

    private static readonly byte[] PngSignature = [
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a
    ];

    public static async Task<AttachmentPreviewAdmissionResult> InspectCompletedAsync(
        Stream source,
        AttachmentManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        AttachmentProtocol.ValidateManifest(manifest);

        if (!source.CanRead)
        {
            throw new ArgumentException("Attachment preview source must be readable.", nameof(source));
        }

        if (source.CanSeek && source.Position != 0)
        {
            throw new ArgumentException(
                "Attachment preview source must be opened at the beginning of the completed file.",
                nameof(source));
        }

        var expectedFormat = GetDeclaredFormat(manifest.ContentType);
        if (expectedFormat is null)
        {
            return AttachmentPreviewAdmissionResult.Rejected(
                AttachmentPreviewRejectionReason.UnsupportedContentType);
        }

        if (manifest.SizeBytes > MaximumRasterPreviewSourceBytes)
        {
            return AttachmentPreviewAdmissionResult.Rejected(
                AttachmentPreviewRejectionReason.SourceTooLarge);
        }

        var content = new byte[checked((int)manifest.SizeBytes)];
        var transferOwnership = false;
        try
        {
            if (!await ReadExactCompletedSourceAsync(source, content, cancellationToken))
            {
                return AttachmentPreviewAdmissionResult.Rejected(
                    AttachmentPreviewRejectionReason.IntegrityMismatch);
            }

            if (!MatchesManifestHash(content, manifest.Sha256))
            {
                return AttachmentPreviewAdmissionResult.Rejected(
                    AttachmentPreviewRejectionReason.IntegrityMismatch);
            }

            var actualFormat = DetectRasterFormat(content);
            if (actualFormat is null || actualFormat != expectedFormat)
            {
                return AttachmentPreviewAdmissionResult.Rejected(
                    AttachmentPreviewRejectionReason.ContentSignatureMismatch);
            }

            if (!TryReadDimensions(content, actualFormat.Value, out var width, out var height))
            {
                return AttachmentPreviewAdmissionResult.Rejected(
                    AttachmentPreviewRejectionReason.InvalidRasterHeader);
            }

            if (!DimensionsWithinPolicy(width, height))
            {
                return AttachmentPreviewAdmissionResult.Rejected(
                    AttachmentPreviewRejectionReason.RasterDimensionsOutOfPolicy);
            }

            var verified = new VerifiedAttachmentPreviewSource(content, actualFormat.Value, width, height);
            transferOwnership = true;
            return AttachmentPreviewAdmissionResult.Allowed(verified);
        }
        finally
        {
            if (!transferOwnership)
            {
                CryptographicOperations.ZeroMemory(content);
            }
        }
    }

    private static AttachmentRasterPreviewFormat? GetDeclaredFormat(string contentType)
    {
        if (string.Equals(contentType, PngContentType, StringComparison.OrdinalIgnoreCase))
        {
            return AttachmentRasterPreviewFormat.Png;
        }

        if (string.Equals(contentType, JpegContentType, StringComparison.OrdinalIgnoreCase))
        {
            return AttachmentRasterPreviewFormat.Jpeg;
        }

        return null;
    }

    private static async Task<bool> ReadExactCompletedSourceAsync(
        Stream source,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < content.Length)
        {
            var read = await source.ReadAsync(content.AsMemory(filled), cancellationToken);
            if (read == 0)
            {
                return false;
            }

            filled += read;
        }

        var trailing = new byte[1];
        try
        {
            return await source.ReadAsync(trailing.AsMemory(), cancellationToken) == 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(trailing);
        }
    }

    private static bool MatchesManifestHash(ReadOnlySpan<byte> content, string expectedSha256)
    {
        var actual = SHA256.HashData(content);
        var expected = Convert.FromHexString(expectedSha256);
        try
        {
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private static AttachmentRasterPreviewFormat? DetectRasterFormat(ReadOnlySpan<byte> content)
    {
        if (content.Length >= PngSignature.Length &&
            content[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            return AttachmentRasterPreviewFormat.Png;
        }

        if (content.Length >= 2 && content[0] == 0xff && content[1] == 0xd8)
        {
            return AttachmentRasterPreviewFormat.Jpeg;
        }

        return null;
    }

    private static bool TryReadDimensions(
        ReadOnlySpan<byte> content,
        AttachmentRasterPreviewFormat format,
        out int width,
        out int height)
        => format switch
        {
            AttachmentRasterPreviewFormat.Png => TryReadPngDimensions(content, out width, out height),
            AttachmentRasterPreviewFormat.Jpeg => TryReadJpegDimensions(content, out width, out height),
            _ => FailDimensions(out width, out height)
        };

    private static bool TryReadPngDimensions(ReadOnlySpan<byte> content, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (content.Length < 29 ||
            !content[..PngSignature.Length].SequenceEqual(PngSignature) ||
            BinaryPrimitives.ReadUInt32BigEndian(content.Slice(8, 4)) != 13 ||
            !content.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        var rawWidth = BinaryPrimitives.ReadUInt32BigEndian(content.Slice(16, 4));
        var rawHeight = BinaryPrimitives.ReadUInt32BigEndian(content.Slice(20, 4));
        var bitDepth = content[24];
        var colorType = content[25];
        var compressionMethod = content[26];
        var filterMethod = content[27];
        var interlaceMethod = content[28];

        if (rawWidth is 0 or > int.MaxValue ||
            rawHeight is 0 or > int.MaxValue ||
            !IsValidPngBitDepth(colorType, bitDepth) ||
            compressionMethod != 0 ||
            filterMethod != 0 ||
            interlaceMethod > 1)
        {
            return false;
        }

        width = checked((int)rawWidth);
        height = checked((int)rawHeight);
        return true;
    }

    private static bool IsValidPngBitDepth(byte colorType, byte bitDepth)
        => colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            4 => bitDepth is 8 or 16,
            6 => bitDepth is 8 or 16,
            _ => false
        };

    private static bool TryReadJpegDimensions(ReadOnlySpan<byte> content, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (content.Length < 4 || content[0] != 0xff || content[1] != 0xd8)
        {
            return false;
        }

        var offset = 2;
        while (offset < content.Length)
        {
            if (content[offset] != 0xff)
            {
                return false;
            }

            while (offset < content.Length && content[offset] == 0xff)
            {
                offset++;
            }

            if (offset >= content.Length)
            {
                return false;
            }

            var marker = content[offset++];
            if (marker == 0x00)
            {
                return false;
            }

            if (marker is 0xd8 or 0xd9 || marker is >= 0xd0 and <= 0xd7 || marker == 0x01)
            {
                if (marker == 0xd9)
                {
                    return false;
                }

                continue;
            }

            if (marker == 0xda)
            {
                return false;
            }

            if (offset + 2 > content.Length)
            {
                return false;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(content.Slice(offset, 2));
            if (segmentLength < 2 || offset + segmentLength > content.Length)
            {
                return false;
            }

            if (IsStartOfFrameMarker(marker))
            {
                if (segmentLength < 11)
                {
                    return false;
                }

                var rawHeight = BinaryPrimitives.ReadUInt16BigEndian(content.Slice(offset + 3, 2));
                var rawWidth = BinaryPrimitives.ReadUInt16BigEndian(content.Slice(offset + 5, 2));
                var componentCount = content[offset + 7];
                var expectedSegmentLength = 8 + (3 * componentCount);
                if (rawWidth == 0 ||
                    rawHeight == 0 ||
                    componentCount == 0 ||
                    segmentLength != expectedSegmentLength)
                {
                    return false;
                }

                width = rawWidth;
                height = rawHeight;
                return true;
            }

            offset += segmentLength;
        }

        return false;
    }

    private static bool IsStartOfFrameMarker(byte marker)
        => marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or
            0xc5 or 0xc6 or 0xc7 or
            0xc9 or 0xca or 0xcb or
            0xcd or 0xce or 0xcf;

    private static bool DimensionsWithinPolicy(int width, int height)
    {
        if (width is < 1 or > MaximumRasterDimension ||
            height is < 1 or > MaximumRasterDimension)
        {
            return false;
        }

        return checked((long)width * height) <= MaximumRasterPixels;
    }

    private static bool FailDimensions(out int width, out int height)
    {
        width = 0;
        height = 0;
        return false;
    }
}
