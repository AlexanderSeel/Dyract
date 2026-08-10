using System.Security.Cryptography;
using System.Text;
using Dyract.Core.Identity;
using Dyract.Protocol;
using Microsoft.Data.Sqlite;

namespace Dyract.Storage;

public enum AttachmentManifestStoreResult
{
    Created,
    Existing,
    Completed
}

public enum AttachmentChunkStoreResult
{
    Stored,
    Duplicate
}

public sealed record AttachmentCompletionReceipt(
    AttachmentCompletionAcknowledgement Acknowledgement,
    DateTimeOffset CompletedAt,
    DateTimeOffset ExpiresAt);

public sealed record AttachmentReceiveCleanupResult(
    int PartialReceivesRemoved,
    int CompletionReceiptsRemoved);

/// <summary>
/// Token returned only after the complete durable receive snapshot was reconstructed and its
/// manifest SHA-256 verified. The caller should promote the staging destination first, then pass
/// this token to MarkCompletedAsync so Dyract persists the final-ACK receipt and releases chunks.
/// </summary>
public sealed class VerifiedAttachmentStaging
{
    internal VerifiedAttachmentStaging(
        string senderPeerId,
        AttachmentManifest manifest,
        byte[] manifestFingerprint)
    {
        SenderPeerId = senderPeerId;
        Manifest = manifest;
        ManifestFingerprint = manifestFingerprint;
    }

    public string SenderPeerId { get; }
    public AttachmentManifest Manifest { get; }
    internal byte[] ManifestFingerprint { get; }
}

public sealed class SqliteAttachmentReceiveStore
{
    private const byte EncryptionFormatVersion = 1;
    private const int EncryptionNonceSize = 12;
    private const int EncryptionTagSize = 16;
    private const int MaximumCompletionReceiptsGlobal = 256;
    private const int MaximumCompletionReceiptsPerSender = 64;

    public static readonly TimeSpan PartialReceiveRetention = TimeSpan.FromDays(14);
    public static readonly TimeSpan CompletionReceiptRetention = TimeSpan.FromDays(7);

    private readonly string _connectionString;
    private readonly ILocalEncryptionKeyProvider _keyProvider;
    private readonly ILocalStore _localStore;
    private readonly TimeProvider _timeProvider;

    public SqliteAttachmentReceiveStore(
        string databasePath,
        ILocalEncryptionKeyProvider keyProvider,
        ILocalStore localStore,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _localStore = localStore ?? throw new ArgumentNullException(nameof(localStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public async Task<AttachmentManifestStoreResult> StoreManifestAsync(
        string senderPeerId,
        AttachmentManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ValidatePeer(senderPeerId);
        AttachmentProtocol.ValidateManifest(manifest);
        await _localStore.InitializeAsync(cancellationToken);

        if (await MatchesCompletedReceiptAsync(senderPeerId, manifest, cancellationToken))
        {
            return AttachmentManifestStoreResult.Completed;
        }

        var existing = await GetManifestAsync(senderPeerId, manifest.AttachmentId, cancellationToken);
        if (existing is not null)
        {
            if (existing != manifest)
            {
                throw new InvalidDataException("AttachmentId collision contains different manifest content.");
            }

            return AttachmentManifestStoreResult.Existing;
        }

        var key = await GetKeyAsync(cancellationToken);
        byte[]? encryptedFileName = null;
        byte[]? encryptedContentType = null;
        byte[]? encryptedSha256 = null;
        try
        {
            encryptedFileName = Protect(
                key,
                Encoding.UTF8.GetBytes(manifest.FileName),
                ManifestContext(senderPeerId, manifest.AttachmentId, "file-name"));
            encryptedContentType = Protect(
                key,
                Encoding.UTF8.GetBytes(manifest.ContentType),
                ManifestContext(senderPeerId, manifest.AttachmentId, "content-type"));
            encryptedSha256 = Protect(
                key,
                Convert.FromHexString(manifest.Sha256),
                ManifestContext(senderPeerId, manifest.AttachmentId, "sha256"));

            var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO attachment_receives(
                    sender_peer_id, attachment_id, file_name, content_type,
                    size_bytes, chunk_size, sha256, created_utc, updated_utc)
                VALUES(
                    $sender_peer_id, $attachment_id, $file_name, $content_type,
                    $size_bytes, $chunk_size, $sha256, $now, $now);
                """;
            command.Parameters.AddWithValue("$sender_peer_id", senderPeerId);
            command.Parameters.AddWithValue("$attachment_id", manifest.AttachmentId);
            command.Parameters.Add("$file_name", SqliteType.Blob).Value = encryptedFileName;
            command.Parameters.Add("$content_type", SqliteType.Blob).Value = encryptedContentType;
            command.Parameters.AddWithValue("$size_bytes", manifest.SizeBytes);
            command.Parameters.AddWithValue("$chunk_size", manifest.ChunkSize);
            command.Parameters.Add("$sha256", SqliteType.Blob).Value = encryptedSha256;
            command.Parameters.AddWithValue("$now", now);

            if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                return AttachmentManifestStoreResult.Created;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            ZeroIfNotNull(encryptedFileName);
            ZeroIfNotNull(encryptedContentType);
            ZeroIfNotNull(encryptedSha256);
        }

        existing = await GetManifestAsync(senderPeerId, manifest.AttachmentId, cancellationToken);
        if (existing == manifest)
        {
            return AttachmentManifestStoreResult.Existing;
        }

        if (await MatchesCompletedReceiptAsync(senderPeerId, manifest, cancellationToken))
        {
            return AttachmentManifestStoreResult.Completed;
        }

        throw new InvalidDataException("AttachmentId collision contains different manifest content.");
    }

    public async Task<AttachmentManifest?> GetManifestAsync(
        string senderPeerId,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        ValidatePeer(senderPeerId);
        ValidateAttachmentId(attachmentId);
        await _localStore.InitializeAsync(cancellationToken);

        var key = await GetKeyAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT file_name, content_type, size_bytes, chunk_size, sha256
                FROM attachment_receives
                WHERE sender_peer_id = $sender_peer_id AND attachment_id = $attachment_id;
                """;
            command.Parameters.AddWithValue("$sender_peer_id", senderPeerId);
            command.Parameters.AddWithValue("$attachment_id", attachmentId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var fileNameBytes = Unprotect(
                key,
                (byte[])reader[0],
                ManifestContext(senderPeerId, attachmentId, "file-name"));
            var contentTypeBytes = Unprotect(
                key,
                (byte[])reader[1],
                ManifestContext(senderPeerId, attachmentId, "content-type"));
            var sha256Bytes = Unprotect(
                key,
                (byte[])reader[4],
                ManifestContext(senderPeerId, attachmentId, "sha256"));

            try
            {
                var manifest = new AttachmentManifest(
                    AttachmentProtocol.CurrentVersion,
                    attachmentId,
                    Encoding.UTF8.GetString(fileNameBytes),
                    Encoding.UTF8.GetString(contentTypeBytes),
                    reader.GetInt64(2),
                    reader.GetInt32(3),
                    Convert.ToHexString(sha256Bytes).ToLowerInvariant());
                AttachmentProtocol.ValidateManifest(manifest);
                return manifest;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fileNameBytes);
                CryptographicOperations.ZeroMemory(contentTypeBytes);
                CryptographicOperations.ZeroMemory(sha256Bytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task<AttachmentChunkStoreResult> StoreChunkAsync(
        string senderPeerId,
        AttachmentChunk chunk,
        CancellationToken cancellationToken = default)
    {
        ValidatePeer(senderPeerId);
        ArgumentNullException.ThrowIfNull(chunk);
        var manifest = await GetManifestAsync(senderPeerId, chunk.AttachmentId, cancellationToken)
            ?? throw new InvalidDataException("Attachment manifest must be active before chunks are accepted.");
        AttachmentProtocol.ValidateChunk(manifest, chunk);

        var key = await GetKeyAsync(cancellationToken);
        byte[]? protectedPayload = null;
        try
        {
            protectedPayload = Protect(
                key,
                chunk.Data,
                ChunkContext(senderPeerId, chunk.AttachmentId, chunk.ChunkIndex));
            var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO attachment_receive_chunks(
                    sender_peer_id, attachment_id, chunk_index, payload, payload_length, received_utc)
                VALUES($sender_peer_id, $attachment_id, $chunk_index, $payload, $payload_length, $received_utc);
                """;
            command.Parameters.AddWithValue("$sender_peer_id", senderPeerId);
            command.Parameters.AddWithValue("$attachment_id", chunk.AttachmentId);
            command.Parameters.AddWithValue("$chunk_index", chunk.ChunkIndex);
            command.Parameters.Add("$payload", SqliteType.Blob).Value = protectedPayload;
            command.Parameters.AddWithValue("$payload_length", chunk.Data.Length);
            command.Parameters.AddWithValue("$received_utc", now);

            if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                await TouchManifestAsync(connection, senderPeerId, chunk.AttachmentId, now, cancellationToken);
                return AttachmentChunkStoreResult.Stored;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            ZeroIfNotNull(protectedPayload);
        }

        var existing = await ReadChunkAsync(senderPeerId, chunk.AttachmentId, chunk.ChunkIndex, cancellationToken);
        if (existing is not null && CryptographicOperations.FixedTimeEquals(existing, chunk.Data))
        {
            CryptographicOperations.ZeroMemory(existing);
            return AttachmentChunkStoreResult.Duplicate;
        }

        if (existing is not null)
        {
            CryptographicOperations.ZeroMemory(existing);
        }

        throw new InvalidDataException("Attachment chunk collision contains different content.");
    }

    public async Task<byte[]?> ReadChunkAsync(
        string senderPeerId,
        string attachmentId,
        int chunkIndex,
        CancellationToken cancellationToken = default)
    {
        ValidatePeer(senderPeerId);
        ValidateAttachmentId(attachmentId);
        if (chunkIndex < 0 || chunkIndex >= AttachmentProtocol.MaximumChunkCount)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIndex));
        }

        await _localStore.InitializeAsync(cancellationToken);
        var key = await GetKeyAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT payload, payload_length
                FROM attachment_receive_chunks
                WHERE sender_peer_id = $sender_peer_id
                  AND attachment_id = $attachment_id
                  AND chunk_index = $chunk_index;
                """;
            command.Parameters.AddWithValue("$sender_peer_id", senderPeerId);
            command.Parameters.AddWithValue("$attachment_id", attachmentId);
            command.Parameters.AddWithValue("$chunk_index", chunkIndex);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var plaintext = Unprotect(
                key,
                (byte[])reader[0],
                ChunkContext(senderPeerId, attachmentId, chunkIndex));
            if (plaintext.Length != reader.GetInt32(1))
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new InvalidDataException("Stored attachment chunk length metadata is inconsistent.");
            }

            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task<IReadOnlyList<AttachmentChunkRange>> GetMissingRangesAsync(
        string senderPeerId,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        var manifest = await GetManifestAsync(senderPeerId, attachmentId, cancellationToken)
            ?? throw new InvalidDataException("Attachment receive state does not exist.");
        var received = new List<int>();

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT chunk_index
            FROM attachment_receive_chunks
            WHERE sender_peer_id = $sender_peer_id AND attachment_id = $attachment_id
            ORDER BY chunk_index;
            """;
        command.Parameters.AddWithValue("$sender_peer_id", senderPeerId);
        command.Parameters.AddWithValue("$attachment_id", attachmentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            received.Add(reader.GetInt32(0));
        }

        return AttachmentProtocol.GetMissingRanges(manifest, received);
    }

    public async Task<VerifiedAttachmentStaging> WriteVerifiedStagingAsync(
        string senderPeerId,
        string attachmentId,
        Stream stagingDestination,
        CancellationToken cancellationToken = default)
    {
        ValidatePeer(senderPeerId);
        ValidateAttachmentId(attachmentId);
        ArgumentNullException.ThrowIfNull(stagingDestination);
        if (!stagingDestination.CanWrite)
        {
            throw new ArgumentException("Attachment staging destination must be writable.", nameof(stagingDestination));
        }

        if (stagingDestination.CanSeek && (stagingDestination.Position != 0 || stagingDestination.Length != 0))
        {
            throw new ArgumentException("Attachment staging destination must be empty and positioned at zero.", nameof(stagingDestination));
        }

        var manifest = await GetManifestAsync(senderPeerId, attachmentId, cancellationToken)
            ?? throw new InvalidDataException("Attachment receive state does not exist.");
        var missing = await GetMissingRangesAsync(senderPeerId, attachmentId, cancellationToken);
        if (missing.Count != 0)
        {
            throw new InvalidDataException("Attachment cannot be reconstructed while chunks are missing.");
        }

        var key = await GetKeyAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT chunk_index, payload, payload_length
                FROM attachment_receive_chunks
                WHERE sender_peer_id = $sender_peer_id AND attachment_id = $attachment_id
                ORDER BY chunk_index;
                """;
            command.Parameters.AddWithValue("$sender_peer_id", senderPeerId);
            command.Parameters.AddWithValue("$attachment_id", attachmentId);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long totalBytes = 0;
            var expectedChunkIndex = 0;
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var chunkIndex = reader.GetInt32(0);
                    if (chunkIndex != expectedChunkIndex)
                    {
                        throw new InvalidDataException("Stored attachment chunks are not contiguous.");
                    }

                    var plaintext = Unprotect(
                        key,
                        (byte[])reader[1],
                        ChunkContext(senderPeerId, attachmentId, chunkIndex));
                    try
                    {
                        if (plaintext.Length != reader.GetInt32(2))
                        {
                            throw new InvalidDataException("Stored attachment chunk length metadata is inconsistent.");
                        }

                        var chunk = AttachmentProtocol.CreateChunk(manifest, chunkIndex, plaintext);
                        await stagingDestination.WriteAsync(chunk.Data.AsMemory(), cancellationToken);
                        hash.AppendData(chunk.Data);
                        totalBytes = checked(totalBytes + chunk.Data.Length);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(plaintext);
                    }

                    expectedChunkIndex++;
                }

                if (expectedChunkIndex != manifest.ChunkCount || totalBytes != manifest.SizeBytes)
                {
                    throw new InvalidDataException("Reconstructed attachment size/chunk count does not match the manifest.");
                }

                await stagingDestination.FlushAsync(cancellationToken);
                var actualHash = hash.GetHashAndReset();
                var expectedHash = Convert.FromHexString(manifest.Sha256);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                    {
                        throw new InvalidDataException("Reconstructed attachment SHA-256 does not match the manifest.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(actualHash);
                    CryptographicOperations.ZeroMemory(expectedHash);
                }

                return new VerifiedAttachmentStaging(
                    senderPeerId,
                    manifest,
                    ComputeManifestFingerprint(manifest));
            }
            catch
            {
                BestEffortClearStaging(stagingDestination);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task<AttachmentCompletionAcknowledgement> MarkCompletedAsync(
        VerifiedAttachmentStaging verifiedStaging,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifiedStaging);
        ValidatePeer(verifiedStaging.SenderPeerId);
        AttachmentProtocol.ValidateManifest(verifiedStaging.Manifest);
        await _localStore.InitializeAsync(cancellationToken);

        var currentManifest = await GetManifestAsync(
            verifiedStaging.SenderPeerId,
            verifiedStaging.Manifest.AttachmentId,
            cancellationToken)
            ?? throw new InvalidDataException("Attachment receive state disappeared before completion could be committed.");
        if (currentManifest != verifiedStaging.Manifest)
        {
            throw new InvalidDataException("Attachment receive manifest changed before completion could be committed.");
        }

        var currentFingerprint = ComputeManifestFingerprint(currentManifest);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(currentFingerprint, verifiedStaging.ManifestFingerprint))
            {
                throw new InvalidDataException("Attachment verification token does not match the active manifest.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(currentFingerprint);
        }

        var missing = await GetMissingRangesAsync(
            verifiedStaging.SenderPeerId,
            verifiedStaging.Manifest.AttachmentId,
            cancellationToken);
        if (missing.Count != 0)
        {
            throw new InvalidDataException("Attachment receive state became incomplete before completion could be committed.");
        }

        var acknowledgement = new AttachmentCompletionAcknowledgement(
            AttachmentProtocol.CurrentVersion,
            verifiedStaging.Manifest.AttachmentId,
            verifiedStaging.Manifest.Sha256);
        AttachmentCompletionAcknowledgementProtocol.ValidateAgainstManifest(
            verifiedStaging.Manifest,
            acknowledgement);

        var key = await GetKeyAsync(cancellationToken);
        byte[]? encryptedFingerprint = null;
        byte[]? encryptedSha256 = null;
        try
        {
            encryptedFingerprint = Protect(
                key,
                verifiedStaging.ManifestFingerprint,
                CompletionContext(verifiedStaging.SenderPeerId, verifiedStaging.Manifest.AttachmentId, "manifest-fingerprint"));
            encryptedSha256 = Protect(
                key,
                Convert.FromHexString(verifiedStaging.Manifest.Sha256),
                CompletionContext(verifiedStaging.SenderPeerId, verifiedStaging.Manifest.AttachmentId, "sha256"));

            var completedAt = _timeProvider.GetUtcNow();
            var expiresAt = completedAt.Add(CompletionReceiptRetention);
            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();

            await DeleteExpiredCompletionReceiptsAsync(connection, transaction, completedAt, cancellationToken);

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO attachment_receive_completions(
                        sender_peer_id, attachment_id, manifest_fingerprint, sha256,
                        completed_utc, expires_utc)
                    VALUES($sender_peer_id, $attachment_id, $manifest_fingerprint, $sha256,
                           $completed_utc, $expires_utc);
                    """;
                insert.Parameters.AddWithValue("$sender_peer_id", verifiedStaging.SenderPeerId);
                insert.Parameters.AddWithValue("$attachment_id", verifiedStaging.Manifest.AttachmentId);
                insert.Parameters.Add("$manifest_fingerprint", SqliteType.Blob).Value = encryptedFingerprint;
                insert.Parameters.Add("$sha256", SqliteType.Blob).Value = encryptedSha256;
                insert.Parameters.AddWithValue("$completed_utc", completedAt.ToUnixTimeMilliseconds());
                insert.Parameters.AddWithValue("$expires_utc", expiresAt.ToUnixTimeMilliseconds());
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var removeActive = connection.CreateCommand())
            {
                removeActive.Transaction = transaction;
                removeActive.CommandText = """
                    DELETE FROM attachment_receives
                    WHERE sender_peer_id = $sender_peer_id AND attachment_id = $attachment_id;
                    """;
                removeActive.Parameters.AddWithValue("$sender_peer_id", verifiedStaging.SenderPeerId);
                removeActive.Parameters.AddWithValue("$attachment_id", verifiedStaging.Manifest.AttachmentId);
                if (await removeActive.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidDataException("Attachment receive state disappeared during completion commit.");
                }
            }

            await EnforceCompletionReceiptBoundsAsync(
                connection,
                transaction,
                verifiedStaging.SenderPeerId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return acknowledgement;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            ZeroIfNotNull(encryptedFingerprint);
            ZeroIfNotNull(encryptedSha256);
        }
    }

    public async Task<AttachmentCompletionReceipt?> GetCompletionReceiptAsync(
        string senderPeerId,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        ValidatePeer(senderPeerId);
        ValidateAttachmentId(attachmentId);
        await _localStore.InitializeAsync(cancellationToken);

        var key = await GetKeyAsync(cancellationToken);
        try
        {
            var row = await ReadCompletionRowAsync(senderPeerId, attachmentId, cancellationToken);
            if (row is null)
            {
                return null;
            }

            var now = _timeProvider.GetUtcNow();
            if (row.ExpiresAt <= now)
            {
                await DeleteCompletionReceiptAsync(senderPeerId, attachmentId, cancellationToken);
                row.Clear();
                return null;
            }

            var sha256 = Unprotect(
                key,
                row.Sha256,
                CompletionContext(senderPeerId, attachmentId, "sha256"));
            try
            {
                var acknowledgement = new AttachmentCompletionAcknowledgement(
                    AttachmentProtocol.CurrentVersion,
                    attachmentId,
                    Convert.ToHexString(sha256).ToLowerInvariant());
                AttachmentCompletionAcknowledgementProtocol.Encode(acknowledgement);
                return new AttachmentCompletionReceipt(acknowledgement, row.CompletedAt, row.ExpiresAt);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sha256);
                row.Clear();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task<AttachmentReceiveCleanupResult> CleanupStaleAsync(
        CancellationToken cancellationToken = default)
    {
        await _localStore.InitializeAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var partialCutoff = now.Subtract(PartialReceiveRetention).ToUnixTimeMilliseconds();

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        int partialRemoved;
        await using (var partial = connection.CreateCommand())
        {
            partial.Transaction = transaction;
            partial.CommandText = "DELETE FROM attachment_receives WHERE updated_utc < $cutoff;";
            partial.Parameters.AddWithValue("$cutoff", partialCutoff);
            partialRemoved = await partial.ExecuteNonQueryAsync(cancellationToken);
        }

        var completionRemoved = await DeleteExpiredCompletionReceiptsAsync(
            connection,
            transaction,
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AttachmentReceiveCleanupResult(partialRemoved, completionRemoved);
    }

    public async Task RemoveAsync(
        string senderPeerId,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        ValidatePeer(senderPeerId);
        ValidateAttachmentId(attachmentId);
        await _localStore.InitializeAsync(cancellationToken);

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM attachment_receives
            WHERE sender_peer_id = $sender_peer_id AND attachment_id = $attachment_id;
            """;
        command.Parameters.AddWithValue("$sender_peer_id", senderPeerId);
        command.Parameters.AddWithValue("$attachment_id", attachmentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> MatchesCompletedReceiptAsync(
        string senderPeerId,
        AttachmentManifest manifest,
        CancellationToken cancellationToken)
    {
        var row = await ReadCompletionRowAsync(senderPeerId, manifest.AttachmentId, cancellationToken);
        if (row is null)
        {
            return false;
        }

        try
        {
            if (row.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                await DeleteCompletionReceiptAsync(senderPeerId, manifest.AttachmentId, cancellationToken);
                return false;
            }

            var key = await GetKeyAsync(cancellationToken);
            try
            {
                var storedFingerprint = Unprotect(
                    key,
                    row.ManifestFingerprint,
                    CompletionContext(senderPeerId, manifest.AttachmentId, "manifest-fingerprint"));
                var actualFingerprint = ComputeManifestFingerprint(manifest);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(storedFingerprint, actualFingerprint))
                    {
                        throw new InvalidDataException("Completed AttachmentId was reused with different manifest content.");
                    }

                    return true;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(storedFingerprint);
                    CryptographicOperations.ZeroMemory(actualFingerprint);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            row.Clear();
        }
    }

    private async Task<CompletionRow?> ReadCompletionRowAsync(
        string senderPeerId,
        string attachmentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT manifest_fingerprint, sha256, completed_utc, expires_utc
            FROM attachment_receive_completions
            WHERE sender_peer_id = $sender_peer_id AND attachment_id = $attachment_id;
            """;
        command.Parameters.AddWithValue("$sender_peer_id", senderPeerId);
        command.Parameters.AddWithValue("$attachment_id", attachmentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CompletionRow(
            ((byte[])reader[0]).ToArray(),
            ((byte[])reader[1]).ToArray(),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)));
    }

    private async Task DeleteCompletionReceiptAsync(
        string senderPeerId,
        string attachmentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM attachment_receive_completions
            WHERE sender_peer_id = $sender_peer_id AND attachment_id = $attachment_id;
            """;
        command.Parameters.AddWithValue("$sender_peer_id", senderPeerId);
        command.Parameters.AddWithValue("$attachment_id", attachmentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> DeleteExpiredCompletionReceiptsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM attachment_receive_completions WHERE expires_utc <= $now;";
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnforceCompletionReceiptBoundsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string senderPeerId,
        CancellationToken cancellationToken)
    {
        await using (var perSender = connection.CreateCommand())
        {
            perSender.Transaction = transaction;
            perSender.CommandText = """
                DELETE FROM attachment_receive_completions
                WHERE rowid IN (
                    SELECT rowid
                    FROM attachment_receive_completions
                    WHERE sender_peer_id = $sender_peer_id
                    ORDER BY completed_utc DESC, attachment_id DESC
                    LIMIT -1 OFFSET $keep
                );
                """;
            perSender.Parameters.AddWithValue("$sender_peer_id", senderPeerId);
            perSender.Parameters.AddWithValue("$keep", MaximumCompletionReceiptsPerSender);
            await perSender.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var global = connection.CreateCommand();
        global.Transaction = transaction;
        global.CommandText = """
            DELETE FROM attachment_receive_completions
            WHERE rowid IN (
                SELECT rowid
                FROM attachment_receive_completions
                ORDER BY completed_utc DESC, sender_peer_id DESC, attachment_id DESC
                LIMIT -1 OFFSET $keep
            );
            """;
        global.Parameters.AddWithValue("$keep", MaximumCompletionReceiptsGlobal);
        await global.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<byte[]> GetKeyAsync(CancellationToken cancellationToken)
    {
        var key = await _keyProvider.GetOrCreateKeyAsync(cancellationToken);
        if (key.Length != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException("Dyract attachment receive storage requires a 256-bit encryption key.");
        }

        return key;
    }

    private static byte[] Protect(byte[] key, ReadOnlySpan<byte> plaintext, string context)
    {
        var nonce = RandomNumberGenerator.GetBytes(EncryptionNonceSize);
        var tag = new byte[EncryptionTagSize];
        var ciphertext = new byte[plaintext.Length];
        var associatedData = Encoding.UTF8.GetBytes(context);
        try
        {
            using var aes = new AesGcm(key, EncryptionTagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
            var result = new byte[1 + nonce.Length + tag.Length + ciphertext.Length];
            result[0] = EncryptionFormatVersion;
            nonce.CopyTo(result, 1);
            tag.CopyTo(result, 1 + nonce.Length);
            ciphertext.CopyTo(result, 1 + nonce.Length + tag.Length);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    private static byte[] Unprotect(byte[] key, byte[] protectedValue, string context)
    {
        if (protectedValue.Length < 1 + EncryptionNonceSize + EncryptionTagSize ||
            protectedValue[0] != EncryptionFormatVersion)
        {
            throw new InvalidDataException("Stored attachment value has an unsupported encrypted format.");
        }

        var nonce = protectedValue.AsSpan(1, EncryptionNonceSize);
        var tag = protectedValue.AsSpan(1 + EncryptionNonceSize, EncryptionTagSize);
        var ciphertext = protectedValue.AsSpan(1 + EncryptionNonceSize + EncryptionTagSize);
        var plaintext = new byte[ciphertext.Length];
        var associatedData = Encoding.UTF8.GetBytes(context);
        try
        {
            using var aes = new AesGcm(key, EncryptionTagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task TouchManifestAsync(
        SqliteConnection connection,
        string senderPeerId,
        string attachmentId,
        long updatedUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE attachment_receives SET updated_utc = $updated_utc
            WHERE sender_peer_id = $sender_peer_id AND attachment_id = $attachment_id;
            """;
        command.Parameters.AddWithValue("$updated_utc", updatedUtc);
        command.Parameters.AddWithValue("$sender_peer_id", senderPeerId);
        command.Parameters.AddWithValue("$attachment_id", attachmentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static byte[] ComputeManifestFingerprint(AttachmentManifest manifest)
    {
        var encoded = AttachmentApplicationFrameProtocol.Encode(new AttachmentManifestApplicationFrame(manifest));
        try
        {
            return SHA256.HashData(encoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private static void BestEffortClearStaging(Stream destination)
    {
        if (!destination.CanSeek || !destination.CanWrite)
        {
            return;
        }

        try
        {
            destination.SetLength(0);
            destination.Position = 0;
        }
        catch
        {
            // The original reconstruction/verification failure is more important than cleanup.
        }
    }

    private static void ValidatePeer(string senderPeerId)
    {
        if (!PeerId.TryParse(senderPeerId, out _))
        {
            throw new ArgumentException("Sender PeerId is invalid.", nameof(senderPeerId));
        }
    }

    private static void ValidateAttachmentId(string attachmentId)
    {
        if (string.IsNullOrWhiteSpace(attachmentId) ||
            attachmentId.Length != AttachmentProtocol.AttachmentIdHexLength ||
            attachmentId.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("AttachmentId is invalid.", nameof(attachmentId));
        }
    }

    private static string ManifestContext(string senderPeerId, string attachmentId, string field)
        => $"dyract:v1:attachment:{senderPeerId}:{attachmentId}:manifest:{field}";

    private static string ChunkContext(string senderPeerId, string attachmentId, int chunkIndex)
        => $"dyract:v1:attachment:{senderPeerId}:{attachmentId}:chunk:{chunkIndex}";

    private static string CompletionContext(string senderPeerId, string attachmentId, string field)
        => $"dyract:v1:attachment:{senderPeerId}:{attachmentId}:completion:{field}";

    private static void ZeroIfNotNull(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private sealed class CompletionRow(
        byte[] manifestFingerprint,
        byte[] sha256,
        DateTimeOffset completedAt,
        DateTimeOffset expiresAt)
    {
        public byte[] ManifestFingerprint { get; } = manifestFingerprint;
        public byte[] Sha256 { get; } = sha256;
        public DateTimeOffset CompletedAt { get; } = completedAt;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public void Clear()
        {
            CryptographicOperations.ZeroMemory(ManifestFingerprint);
            CryptographicOperations.ZeroMemory(Sha256);
        }
    }
}
