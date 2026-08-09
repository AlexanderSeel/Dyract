using System.Buffers.Binary;
using System.Text;

namespace Dyract.Protocol;

public abstract record AttachmentApplicationFrame(int Version, string AttachmentId);

public sealed record AttachmentManifestApplicationFrame(AttachmentManifest Manifest)
    : AttachmentApplicationFrame(Manifest.Version, Manifest.AttachmentId);

public sealed record AttachmentChunkApplicationFrame(AttachmentChunk Chunk)
    : AttachmentApplicationFrame(Chunk.Version, Chunk.AttachmentId);

public sealed record AttachmentResumeApplicationFrame(
    int Version,
    string AttachmentId,
    IReadOnlyList<AttachmentChunkRange> MissingRanges)
    : AttachmentApplicationFrame(Version, AttachmentId);

public static class AttachmentApplicationFrameProtocol
{
    private static readonly byte[] Magic = "DYRA"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private const byte ManifestFrameType = 1;
    private const byte ChunkFrameType = 2;
    private const byte ResumeFrameType = 3;
    private const int HeaderSize = 6;
    private const int IdSize = 16;
    private const int HashSize = 32;
    private const int MaximumResumeRanges = 2048;

    public const int MaximumEncodedFrameBytes = 128 * 1024;

    public static byte[] Encode(AttachmentApplicationFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return frame switch
        {
            AttachmentManifestApplicationFrame manifest => EncodeManifest(manifest.Manifest),
            AttachmentChunkApplicationFrame chunk => EncodeChunk(chunk.Chunk),
            AttachmentResumeApplicationFrame resume => EncodeResume(resume),
            _ => throw new ArgumentException("Attachment application frame type is not supported.", nameof(frame))
        };
    }

    public static AttachmentApplicationFrame Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < HeaderSize || encoded.Length > MaximumEncodedFrameBytes)
        {
            throw new InvalidDataException("Attachment application frame size is invalid.");
        }

        if (!encoded[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("Attachment application frame magic is invalid.");
        }

        var version = encoded[4];
        if (version != AttachmentProtocol.CurrentVersion)
        {
            throw new InvalidDataException("Attachment application frame version is not supported.");
        }

        return encoded[5] switch
        {
            ManifestFrameType => DecodeManifest(encoded),
            ChunkFrameType => DecodeChunk(encoded),
            ResumeFrameType => DecodeResume(encoded),
            _ => throw new InvalidDataException("Attachment application frame type is not supported.")
        };
    }

    public static void ValidateResumeRequest(
        AttachmentManifest manifest,
        AttachmentResumeApplicationFrame resume)
    {
        AttachmentProtocol.ValidateManifest(manifest);
        ArgumentNullException.ThrowIfNull(resume);

        if (resume.Version != AttachmentProtocol.CurrentVersion ||
            !string.Equals(resume.AttachmentId, manifest.AttachmentId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Attachment resume request does not belong to this manifest/version.");
        }

        if (resume.MissingRanges.Count > MaximumResumeRanges)
        {
            throw new InvalidDataException("Attachment resume request contains too many ranges.");
        }

        var previousEnd = 0;
        foreach (var range in resume.MissingRanges)
        {
            if (range.Count <= 0 || range.StartChunkIndex < previousEnd || range.EndChunkIndexExclusive > manifest.ChunkCount)
            {
                throw new InvalidDataException("Attachment resume ranges must be positive, ordered, non-overlapping and within the manifest.");
            }

            previousEnd = range.EndChunkIndexExclusive;
        }
    }

    private static byte[] EncodeManifest(AttachmentManifest manifest)
    {
        AttachmentProtocol.ValidateManifest(manifest);
        var fileName = StrictUtf8.GetBytes(manifest.FileName);
        var contentType = StrictUtf8.GetBytes(manifest.ContentType);
        if (fileName.Length > ushort.MaxValue || contentType.Length > ushort.MaxValue)
        {
            throw new InvalidDataException("Attachment manifest text metadata is too large.");
        }

        var length = checked(HeaderSize + IdSize + sizeof(long) + sizeof(int) + HashSize +
                             sizeof(ushort) + sizeof(ushort) + fileName.Length + contentType.Length);
        var encoded = new byte[length];
        WriteHeader(encoded, ManifestFrameType);
        var offset = HeaderSize;
        offset = WriteBytes(encoded, offset, Convert.FromHexString(manifest.AttachmentId));
        BinaryPrimitives.WriteInt64BigEndian(encoded.AsSpan(offset, sizeof(long)), manifest.SizeBytes);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(offset, sizeof(int)), manifest.ChunkSize);
        offset += sizeof(int);
        offset = WriteBytes(encoded, offset, Convert.FromHexString(manifest.Sha256));
        BinaryPrimitives.WriteUInt16BigEndian(encoded.AsSpan(offset, sizeof(ushort)), checked((ushort)fileName.Length));
        offset += sizeof(ushort);
        BinaryPrimitives.WriteUInt16BigEndian(encoded.AsSpan(offset, sizeof(ushort)), checked((ushort)contentType.Length));
        offset += sizeof(ushort);
        offset = WriteBytes(encoded, offset, fileName);
        WriteBytes(encoded, offset, contentType);
        return encoded;
    }

    private static byte[] EncodeChunk(AttachmentChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Version != AttachmentProtocol.CurrentVersion ||
            chunk.Data is null ||
            chunk.Data.Length < 1 ||
            chunk.Data.Length > AttachmentProtocol.ChunkSizeBytes ||
            !IsCanonicalId(chunk.AttachmentId) ||
            chunk.ChunkIndex < 0 ||
            chunk.Offset < 0)
        {
            throw new InvalidDataException("Attachment chunk frame is invalid.");
        }

        var length = checked(HeaderSize + IdSize + sizeof(int) + sizeof(long) + sizeof(int) + chunk.Data.Length);
        if (length > MaximumEncodedFrameBytes)
        {
            throw new InvalidDataException("Attachment chunk frame exceeds the application-frame limit.");
        }

        var encoded = new byte[length];
        WriteHeader(encoded, ChunkFrameType);
        var offset = HeaderSize;
        offset = WriteBytes(encoded, offset, Convert.FromHexString(chunk.AttachmentId));
        BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(offset, sizeof(int)), chunk.ChunkIndex);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt64BigEndian(encoded.AsSpan(offset, sizeof(long)), chunk.Offset);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(offset, sizeof(int)), chunk.Data.Length);
        offset += sizeof(int);
        WriteBytes(encoded, offset, chunk.Data);
        return encoded;
    }

    private static byte[] EncodeResume(AttachmentResumeApplicationFrame resume)
    {
        if (resume.Version != AttachmentProtocol.CurrentVersion ||
            !IsCanonicalId(resume.AttachmentId) ||
            resume.MissingRanges is null ||
            resume.MissingRanges.Count > MaximumResumeRanges)
        {
            throw new InvalidDataException("Attachment resume frame is invalid.");
        }

        var length = checked(HeaderSize + IdSize + sizeof(ushort) + resume.MissingRanges.Count * (sizeof(int) * 2));
        var encoded = new byte[length];
        WriteHeader(encoded, ResumeFrameType);
        var offset = HeaderSize;
        offset = WriteBytes(encoded, offset, Convert.FromHexString(resume.AttachmentId));
        BinaryPrimitives.WriteUInt16BigEndian(
            encoded.AsSpan(offset, sizeof(ushort)),
            checked((ushort)resume.MissingRanges.Count));
        offset += sizeof(ushort);

        foreach (var range in resume.MissingRanges)
        {
            if (range.StartChunkIndex < 0 || range.Count <= 0)
            {
                throw new InvalidDataException("Attachment resume range is invalid.");
            }

            BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(offset, sizeof(int)), range.StartChunkIndex);
            offset += sizeof(int);
            BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(offset, sizeof(int)), range.Count);
            offset += sizeof(int);
        }

        return encoded;
    }

    private static AttachmentManifestApplicationFrame DecodeManifest(ReadOnlySpan<byte> encoded)
    {
        var minimum = HeaderSize + IdSize + sizeof(long) + sizeof(int) + HashSize + sizeof(ushort) + sizeof(ushort);
        if (encoded.Length < minimum)
        {
            throw new InvalidDataException("Attachment manifest frame is truncated.");
        }

        var offset = HeaderSize;
        var attachmentId = Convert.ToHexString(Read(encoded, ref offset, IdSize)).ToLowerInvariant();
        var sizeBytes = ReadInt64(encoded, ref offset);
        var chunkSize = ReadInt32(encoded, ref offset);
        var sha256 = Convert.ToHexString(Read(encoded, ref offset, HashSize)).ToLowerInvariant();
        var fileNameLength = ReadUInt16(encoded, ref offset);
        var contentTypeLength = ReadUInt16(encoded, ref offset);
        var fileName = DecodeUtf8(Read(encoded, ref offset, fileNameLength));
        var contentType = DecodeUtf8(Read(encoded, ref offset, contentTypeLength));
        EnsureFullyConsumed(encoded, offset);

        var manifest = new AttachmentManifest(
            AttachmentProtocol.CurrentVersion,
            attachmentId,
            fileName,
            contentType,
            sizeBytes,
            chunkSize,
            sha256);
        AttachmentProtocol.ValidateManifest(manifest);
        return new AttachmentManifestApplicationFrame(manifest);
    }

    private static AttachmentChunkApplicationFrame DecodeChunk(ReadOnlySpan<byte> encoded)
    {
        var minimum = HeaderSize + IdSize + sizeof(int) + sizeof(long) + sizeof(int) + 1;
        if (encoded.Length < minimum)
        {
            throw new InvalidDataException("Attachment chunk frame is truncated.");
        }

        var offset = HeaderSize;
        var attachmentId = Convert.ToHexString(Read(encoded, ref offset, IdSize)).ToLowerInvariant();
        var chunkIndex = ReadInt32(encoded, ref offset);
        var chunkOffset = ReadInt64(encoded, ref offset);
        var payloadLength = ReadInt32(encoded, ref offset);
        if (payloadLength is < 1 or > AttachmentProtocol.ChunkSizeBytes)
        {
            throw new InvalidDataException("Attachment chunk payload length is invalid.");
        }

        var payload = Read(encoded, ref offset, payloadLength).ToArray();
        EnsureFullyConsumed(encoded, offset);
        return new AttachmentChunkApplicationFrame(new AttachmentChunk(
            AttachmentProtocol.CurrentVersion,
            attachmentId,
            chunkIndex,
            chunkOffset,
            payload));
    }

    private static AttachmentResumeApplicationFrame DecodeResume(ReadOnlySpan<byte> encoded)
    {
        var minimum = HeaderSize + IdSize + sizeof(ushort);
        if (encoded.Length < minimum)
        {
            throw new InvalidDataException("Attachment resume frame is truncated.");
        }

        var offset = HeaderSize;
        var attachmentId = Convert.ToHexString(Read(encoded, ref offset, IdSize)).ToLowerInvariant();
        var rangeCount = ReadUInt16(encoded, ref offset);
        if (rangeCount > MaximumResumeRanges)
        {
            throw new InvalidDataException("Attachment resume frame contains too many ranges.");
        }

        var ranges = new List<AttachmentChunkRange>(rangeCount);
        for (var index = 0; index < rangeCount; index++)
        {
            var start = ReadInt32(encoded, ref offset);
            var count = ReadInt32(encoded, ref offset);
            if (start < 0 || count <= 0)
            {
                throw new InvalidDataException("Attachment resume frame contains an invalid range.");
            }

            ranges.Add(new AttachmentChunkRange(start, count));
        }

        EnsureFullyConsumed(encoded, offset);
        return new AttachmentResumeApplicationFrame(
            AttachmentProtocol.CurrentVersion,
            attachmentId,
            ranges);
    }

    private static void WriteHeader(Span<byte> destination, byte frameType)
    {
        Magic.CopyTo(destination);
        destination[4] = AttachmentProtocol.CurrentVersion;
        destination[5] = frameType;
    }

    private static int WriteBytes(Span<byte> destination, int offset, ReadOnlySpan<byte> value)
    {
        value.CopyTo(destination[offset..]);
        return checked(offset + value.Length);
    }

    private static ReadOnlySpan<byte> Read(ReadOnlySpan<byte> source, ref int offset, int length)
    {
        if (length < 0 || offset < 0 || offset > source.Length - length)
        {
            throw new InvalidDataException("Attachment application frame is truncated or malformed.");
        }

        var value = source.Slice(offset, length);
        offset = checked(offset + length);
        return value;
    }

    private static int ReadInt32(ReadOnlySpan<byte> source, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt32BigEndian(Read(source, ref offset, sizeof(int)));
        return value;
    }

    private static long ReadInt64(ReadOnlySpan<byte> source, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt64BigEndian(Read(source, ref offset, sizeof(long)));
        return value;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> source, ref int offset)
    {
        var value = BinaryPrimitives.ReadUInt16BigEndian(Read(source, ref offset, sizeof(ushort)));
        return value;
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> value)
    {
        try
        {
            return StrictUtf8.GetString(value);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Attachment application frame contains invalid UTF-8.", exception);
        }
    }

    private static void EnsureFullyConsumed(ReadOnlySpan<byte> source, int offset)
    {
        if (offset != source.Length)
        {
            throw new InvalidDataException("Attachment application frame contains trailing data.");
        }
    }

    private static bool IsCanonicalId(string? value)
    {
        if (value is null || value.Length != AttachmentProtocol.AttachmentIdHexLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
