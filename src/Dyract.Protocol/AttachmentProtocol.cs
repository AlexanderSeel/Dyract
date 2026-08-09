using System.Security.Cryptography;

namespace Dyract.Protocol;

public sealed record AttachmentManifest(
    int Version,
    string AttachmentId,
    string FileName,
    string ContentType,
    long SizeBytes,
    int ChunkSize,
    string Sha256)
{
    public int ChunkCount => checked((int)((SizeBytes + ChunkSize - 1) / ChunkSize));
}

public sealed record AttachmentChunk(
    int Version,
    string AttachmentId,
    int ChunkIndex,
    long Offset,
    byte[] Data);

public sealed record AttachmentChunkRange(int StartChunkIndex, int Count)
{
    public int EndChunkIndexExclusive => checked(StartChunkIndex + Count);
}

public static class AttachmentProtocol
{
    public const int CurrentVersion = 1;
    public const int ChunkSizeBytes = 64 * 1024;
    public const long MaximumAttachmentBytes = 100L * 1024 * 1024;
    public const int MaximumFileNameLength = 128;
    public const int MaximumContentTypeLength = 127;
    public const int AttachmentIdHexLength = 32;
    public const int Sha256HexLength = 64;

    public static AttachmentManifest CreateManifest(
        string fileName,
        string? contentType,
        long sizeBytes,
        ReadOnlySpan<byte> sha256,
        string? attachmentId = null)
    {
        if (sha256.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException("Attachment SHA-256 must contain exactly 32 bytes.", nameof(sha256));
        }

        var manifest = new AttachmentManifest(
            CurrentVersion,
            attachmentId ?? CreateAttachmentId(),
            fileName,
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            sizeBytes,
            ChunkSizeBytes,
            Convert.ToHexString(sha256).ToLowerInvariant());

        ValidateManifest(manifest);
        return manifest;
    }

    public static void ValidateManifest(AttachmentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.Version != CurrentVersion)
        {
            throw new InvalidDataException("Attachment manifest version is not supported.");
        }

        if (!IsCanonicalHex(manifest.AttachmentId, AttachmentIdHexLength))
        {
            throw new InvalidDataException("AttachmentId must be a canonical 128-bit lowercase hexadecimal identifier.");
        }

        ValidateFileName(manifest.FileName);
        ValidateContentType(manifest.ContentType);

        if (manifest.SizeBytes is < 1 or > MaximumAttachmentBytes)
        {
            throw new InvalidDataException($"Attachment size must be between 1 and {MaximumAttachmentBytes} bytes.");
        }

        if (manifest.ChunkSize != ChunkSizeBytes)
        {
            throw new InvalidDataException($"Attachment chunk size must be exactly {ChunkSizeBytes} bytes for protocol v1.");
        }

        if (!IsCanonicalHex(manifest.Sha256, Sha256HexLength))
        {
            throw new InvalidDataException("Attachment SHA-256 must be canonical lowercase hexadecimal.");
        }
    }

    public static AttachmentChunk CreateChunk(
        AttachmentManifest manifest,
        int chunkIndex,
        ReadOnlySpan<byte> data)
    {
        ValidateManifest(manifest);
        var expectedLength = GetExpectedChunkLength(manifest, chunkIndex);
        if (data.Length != expectedLength)
        {
            throw new ArgumentException(
                $"Chunk {chunkIndex} must contain exactly {expectedLength} bytes.",
                nameof(data));
        }

        return new AttachmentChunk(
            CurrentVersion,
            manifest.AttachmentId,
            chunkIndex,
            checked((long)chunkIndex * manifest.ChunkSize),
            data.ToArray());
    }

    public static void ValidateChunk(AttachmentManifest manifest, AttachmentChunk chunk)
    {
        ValidateManifest(manifest);
        ArgumentNullException.ThrowIfNull(chunk);

        if (chunk.Version != CurrentVersion ||
            !string.Equals(chunk.AttachmentId, manifest.AttachmentId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Attachment chunk does not belong to this manifest/version.");
        }

        var expectedLength = GetExpectedChunkLength(manifest, chunk.ChunkIndex);
        var expectedOffset = checked((long)chunk.ChunkIndex * manifest.ChunkSize);
        if (chunk.Offset != expectedOffset || chunk.Data is null || chunk.Data.Length != expectedLength)
        {
            throw new InvalidDataException("Attachment chunk offset or payload length is invalid.");
        }
    }

    public static IReadOnlyList<AttachmentChunkRange> GetMissingRanges(
        AttachmentManifest manifest,
        IEnumerable<int> receivedChunkIndices)
    {
        ValidateManifest(manifest);
        ArgumentNullException.ThrowIfNull(receivedChunkIndices);

        var received = new bool[manifest.ChunkCount];
        foreach (var chunkIndex in receivedChunkIndices)
        {
            if (chunkIndex < 0 || chunkIndex >= received.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(receivedChunkIndices),
                    $"Received chunk index {chunkIndex} is outside this attachment.");
            }

            received[chunkIndex] = true;
        }

        var missing = new List<AttachmentChunkRange>();
        var index = 0;
        while (index < received.Length)
        {
            if (received[index])
            {
                index++;
                continue;
            }

            var start = index;
            while (index < received.Length && !received[index])
            {
                index++;
            }

            missing.Add(new AttachmentChunkRange(start, index - start));
        }

        return missing;
    }

    public static async Task<bool> VerifySha256Async(
        Stream source,
        AttachmentManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateManifest(manifest);
        if (!source.CanRead)
        {
            throw new ArgumentException("Attachment stream must be readable.", nameof(source));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkSizeBytes];
        long totalBytes = 0;

        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                totalBytes = checked(totalBytes + read);
                if (totalBytes > manifest.SizeBytes)
                {
                    return false;
                }

                hash.AppendData(buffer, 0, read);
            }

            if (totalBytes != manifest.SizeBytes)
            {
                return false;
            }

            var actualHash = hash.GetHashAndReset();
            var expectedHash = Convert.FromHexString(manifest.Sha256);
            try
            {
                return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actualHash);
                CryptographicOperations.ZeroMemory(expectedHash);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static int GetExpectedChunkLength(AttachmentManifest manifest, int chunkIndex)
    {
        if (chunkIndex < 0 || chunkIndex >= manifest.ChunkCount)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIndex), "Chunk index is outside this attachment.");
        }

        var offset = checked((long)chunkIndex * manifest.ChunkSize);
        return checked((int)Math.Min(manifest.ChunkSize, manifest.SizeBytes - offset));
    }

    private static string CreateAttachmentId()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Length > MaximumFileNameLength ||
            !string.Equals(fileName, fileName.Trim(), StringComparison.Ordinal) ||
            fileName is "." or "..")
        {
            throw new InvalidDataException("Attachment filename is empty, non-canonical, or too long.");
        }

        foreach (var character in fileName)
        {
            if (char.IsControl(character) || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
            {
                throw new InvalidDataException("Attachment filename contains path or control characters.");
            }
        }
    }

    private static void ValidateContentType(string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType) ||
            contentType.Length > MaximumContentTypeLength ||
            !string.Equals(contentType, contentType.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Attachment content type is empty, non-canonical, or too long.");
        }

        var slash = contentType.IndexOf('/');
        if (slash <= 0 || slash != contentType.LastIndexOf('/') || slash == contentType.Length - 1 ||
            !IsMimeToken(contentType.AsSpan(0, slash)) ||
            !IsMimeToken(contentType.AsSpan(slash + 1)))
        {
            throw new InvalidDataException("Attachment content type must be a simple MIME type without parameters.");
        }
    }

    private static bool IsMimeToken(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '!' and not '#' and not '$' and not '&' and not '^' and not '_' and not '.' and not '+' and not '-')
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    private static bool IsCanonicalHex(string? value, int expectedLength)
    {
        if (value is null || value.Length != expectedLength)
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
