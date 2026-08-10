using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Dyract.Protocol;

/// <summary>
/// Builds a bounded attachment manifest from a readable source and replays that source as
/// canonical protocol chunks. The caller may reopen provider-backed files between inspection
/// and chunk enumeration; QueueAsync performs a second whole-snapshot hash check before commit.
/// </summary>
public static class AttachmentStreamSnapshot
{
    public static async Task<AttachmentManifest> InspectAsync(
        Stream source,
        string fileName,
        string? contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("Attachment source must be readable.", nameof(source));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[AttachmentProtocol.ChunkSizeBytes];
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
                if (totalBytes > AttachmentProtocol.MaximumAttachmentBytes)
                {
                    throw new InvalidDataException(
                        $"Attachment exceeds the {AttachmentProtocol.MaximumAttachmentBytes}-byte protocol limit.");
                }

                hash.AppendData(buffer, 0, read);
            }

            if (totalBytes == 0)
            {
                throw new InvalidDataException("Attachment source is empty.");
            }

            var sha256 = hash.GetHashAndReset();
            try
            {
                return AttachmentProtocol.CreateManifest(fileName, contentType, totalBytes, sha256);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sha256);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    public static async IAsyncEnumerable<AttachmentChunk> ReadChunksAsync(
        Stream source,
        AttachmentManifest manifest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        AttachmentProtocol.ValidateManifest(manifest);
        if (!source.CanRead)
        {
            throw new ArgumentException("Attachment source must be readable.", nameof(source));
        }

        var buffer = new byte[AttachmentProtocol.ChunkSizeBytes];
        try
        {
            for (var chunkIndex = 0; chunkIndex < manifest.ChunkCount; chunkIndex++)
            {
                var offset = checked((long)chunkIndex * manifest.ChunkSize);
                var expectedLength = checked((int)Math.Min(manifest.ChunkSize, manifest.SizeBytes - offset));
                var filled = 0;
                while (filled < expectedLength)
                {
                    var read = await source.ReadAsync(
                        buffer.AsMemory(filled, expectedLength - filled),
                        cancellationToken);
                    if (read == 0)
                    {
                        throw new InvalidDataException("Attachment source changed or ended before the inspected snapshot.");
                    }

                    filled += read;
                }

                var chunk = AttachmentProtocol.CreateChunk(
                    manifest,
                    chunkIndex,
                    buffer.AsSpan(0, expectedLength));
                try
                {
                    yield return chunk;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(chunk.Data);
                    CryptographicOperations.ZeroMemory(buffer.AsSpan(0, expectedLength));
                }
            }

            var trailing = await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
            if (trailing != 0)
            {
                throw new InvalidDataException("Attachment source changed or grew after the inspected snapshot.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
