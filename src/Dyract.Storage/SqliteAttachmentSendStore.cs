using System.Security.Cryptography;
using System.Text;
using Dyract.Core.Identity;
using Dyract.Protocol;
using Microsoft.Data.Sqlite;

namespace Dyract.Storage;

public sealed class SqliteAttachmentSendStore : IAttachmentSendStore
{
    private const byte EncryptionFormatVersion = 1;
    private const int EncryptionNonceSize = 12;
    private const int EncryptionTagSize = 16;
    private const int MaximumTransferBatch = 16;
    private const int MaximumChunksPerTransferBatch = 64;
    private const int MaximumFailureCodeLength = 64;

    private readonly string _connectionString;
    private readonly ILocalEncryptionKeyProvider _keyProvider;
    private readonly ILocalStore _localStore;

    public SqliteAttachmentSendStore(
        string databasePath,
        ILocalEncryptionKeyProvider keyProvider,
        ILocalStore localStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _localStore = localStore ?? throw new ArgumentNullException(nameof(localStore));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public async Task QueueAsync(
        string senderPeerId,
        string recipientPeerId,
        AttachmentManifest manifest,
        IAsyncEnumerable<AttachmentChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        ValidatePeerPair(senderPeerId, recipientPeerId);
        AttachmentProtocol.ValidateManifest(manifest);
        ArgumentNullException.ThrowIfNull(chunks);
        await _localStore.InitializeAsync(cancellationToken);

        var key = await GetKeyAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();

            if (await SendExistsAsync(
                    connection,
                    transaction,
                    senderPeerId,
                    recipientPeerId,
                    manifest.AttachmentId,
                    cancellationToken))
            {
                throw new InvalidOperationException("AttachmentId is already queued for this peer.");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var expectedChunkIndex = 0;
            await using var enumerator = chunks.GetAsyncEnumerator(cancellationToken);
            while (await enumerator.MoveNextAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = enumerator.Current ?? throw new InvalidDataException("Attachment chunk stream returned null.");
                AttachmentProtocol.ValidateChunk(manifest, chunk);
                if (chunk.ChunkIndex != expectedChunkIndex)
                {
                    throw new InvalidDataException("Attachment chunks must be queued exactly once in canonical index order.");
                }

                hash.AppendData(chunk.Data);
                var protectedPayload = Protect(
                    key,
                    chunk.Data,
                    ChunkContext(senderPeerId, recipientPeerId, manifest.AttachmentId, chunk.ChunkIndex));
                try
                {
                    await InsertChunkAsync(
                        connection,
                        transaction,
                        senderPeerId,
                        recipientPeerId,
                        manifest.AttachmentId,
                        chunk,
                        protectedPayload,
                        cancellationToken);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(protectedPayload);
                }

                expectedChunkIndex++;
            }

            if (expectedChunkIndex != manifest.ChunkCount)
            {
                throw new InvalidDataException("Attachment chunk stream ended before the complete manifest snapshot was queued.");
            }

            var actualHash = hash.GetHashAndReset();
            var expectedHash = Convert.FromHexString(manifest.Sha256);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                {
                    throw new InvalidDataException("Queued attachment chunks do not match the manifest SHA-256.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actualHash);
                CryptographicOperations.ZeroMemory(expectedHash);
            }

            var encryptedFileName = ProtectText(
                key,
                manifest.FileName,
                ManifestContext(senderPeerId, recipientPeerId, manifest.AttachmentId, "file-name"));
            var encryptedContentType = ProtectText(
                key,
                manifest.ContentType,
                ManifestContext(senderPeerId, recipientPeerId, manifest.AttachmentId, "content-type"));
            var encryptedSha256 = Protect(
                key,
                Convert.FromHexString(manifest.Sha256),
                ManifestContext(senderPeerId, recipientPeerId, manifest.AttachmentId, "sha256"));

            try
            {
                await InsertSendAsync(
                    connection,
                    transaction,
                    senderPeerId,
                    recipientPeerId,
                    manifest,
                    encryptedFileName,
                    encryptedContentType,
                    encryptedSha256,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (SqliteException exception) when (
                exception.Message.Contains("attachment_send_quota", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Attachment send queue quota is exceeded.", exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encryptedFileName);
                CryptographicOperations.ZeroMemory(encryptedContentType);
                CryptographicOperations.ZeroMemory(encryptedSha256);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task<IReadOnlyList<DueAttachmentSend>> GetDueAsync(
        DateTimeOffset dueAtOrBefore,
        int transferLimit = 4,
        int chunksPerTransfer = 16,
        CancellationToken cancellationToken = default)
    {
        if (transferLimit is < 1 or > MaximumTransferBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(transferLimit));
        }

        if (chunksPerTransfer is < 1 or > MaximumChunksPerTransferBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(chunksPerTransfer));
        }

        await _localStore.InitializeAsync(cancellationToken);
        var key = await GetKeyAsync(cancellationToken);
        try
        {
            var result = new List<DueAttachmentSend>();
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT sender_peer_id, recipient_peer_id, attachment_id,
                       file_name, content_type, size_bytes, chunk_size, sha256,
                       attempts, manifest_acknowledged
                FROM attachment_sends
                WHERE next_attempt_utc <= $due
                ORDER BY next_attempt_utc, created_utc, attachment_id
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$due", dueAtOrBefore.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$limit", transferLimit);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<SendRow>();
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new SendRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    ((byte[])reader[3]).ToArray(),
                    ((byte[])reader[4]).ToArray(),
                    reader.GetInt64(5),
                    reader.GetInt32(6),
                    ((byte[])reader[7]).ToArray(),
                    reader.GetInt32(8),
                    reader.GetInt32(9) != 0));
            }

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var manifest = DecryptManifest(key, row);
                var chunks = await ReadPendingChunksAsync(
                    connection,
                    key,
                    row.SenderPeerId,
                    row.RecipientPeerId,
                    manifest,
                    chunksPerTransfer,
                    cancellationToken);
                var completionProbe = row.ManifestAcknowledged && chunks.Count == 0;
                result.Add(new DueAttachmentSend(
                    row.SenderPeerId,
                    row.RecipientPeerId,
                    manifest,
                    chunks,
                    SendManifest: !row.ManifestAcknowledged || completionProbe,
                    CompletionProbe: completionProbe,
                    row.Attempts));
                row.Clear();
            }

            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public Task<bool> RecordAttemptSentAsync(
        string senderPeerId,
        string recipientPeerId,
        string attachmentId,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default)
        => RecordAttemptAsync(
            senderPeerId,
            recipientPeerId,
            attachmentId,
            failureCode: null,
            nextAttemptAt,
            cancellationToken);

    public async Task<bool> RecordAttemptFailureAsync(
        string senderPeerId,
        string recipientPeerId,
        string attachmentId,
        string failureCode,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        ValidateFailureCode(failureCode);
        return await RecordAttemptAsync(
            senderPeerId,
            recipientPeerId,
            attachmentId,
            failureCode,
            nextAttemptAt,
            cancellationToken);
    }

    public async Task<bool> ApplyResumeAsync(
        string senderPeerId,
        string recipientPeerId,
        AttachmentResumeApplicationFrame resume,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        ValidatePeerPair(senderPeerId, recipientPeerId);
        ArgumentNullException.ThrowIfNull(resume);
        await _localStore.InitializeAsync(cancellationToken);

        var key = await GetKeyAsync(cancellationToken);
        try
        {
            var manifest = await ReadManifestAsync(
                senderPeerId,
                recipientPeerId,
                resume.AttachmentId,
                key,
                cancellationToken);
            if (manifest is null)
            {
                return false;
            }

            AttachmentApplicationFrameProtocol.ValidateResumeRequest(manifest, resume);

            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            await using (var acknowledge = connection.CreateCommand())
            {
                acknowledge.Transaction = transaction;
                acknowledge.CommandText = """
                    UPDATE attachment_send_chunks
                    SET acknowledged = 1
                    WHERE sender_peer_id = $sender
                      AND recipient_peer_id = $recipient
                      AND attachment_id = $attachment_id;
                    """;
                AddScopeParameters(acknowledge, senderPeerId, recipientPeerId, resume.AttachmentId);
                await acknowledge.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var range in resume.MissingRanges)
            {
                await using var missing = connection.CreateCommand();
                missing.Transaction = transaction;
                missing.CommandText = """
                    UPDATE attachment_send_chunks
                    SET acknowledged = 0
                    WHERE sender_peer_id = $sender
                      AND recipient_peer_id = $recipient
                      AND attachment_id = $attachment_id
                      AND chunk_index >= $start
                      AND chunk_index < $end;
                    """;
                AddScopeParameters(missing, senderPeerId, recipientPeerId, resume.AttachmentId);
                missing.Parameters.AddWithValue("$start", range.StartChunkIndex);
                missing.Parameters.AddWithValue("$end", range.EndChunkIndexExclusive);
                await missing.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE attachment_sends
                SET manifest_acknowledged = 1,
                    attempts = 0,
                    last_failure = NULL,
                    next_attempt_utc = $next_attempt,
                    updated_utc = $updated
                WHERE sender_peer_id = $sender
                  AND recipient_peer_id = $recipient
                  AND attachment_id = $attachment_id;
                """;
            AddScopeParameters(update, senderPeerId, recipientPeerId, resume.AttachmentId);
            update.Parameters.AddWithValue("$next_attempt", nextAttemptAt.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var changed = await update.ExecuteNonQueryAsync(cancellationToken) == 1;
            await transaction.CommitAsync(cancellationToken);
            return changed;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task<bool> MarkCompletedAsync(
        string senderPeerId,
        string recipientPeerId,
        AttachmentCompletionAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        ValidatePeerPair(senderPeerId, recipientPeerId);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        await _localStore.InitializeAsync(cancellationToken);

        var key = await GetKeyAsync(cancellationToken);
        try
        {
            var manifest = await ReadManifestAsync(
                senderPeerId,
                recipientPeerId,
                acknowledgement.AttachmentId,
                key,
                cancellationToken);
            if (manifest is null)
            {
                return false;
            }

            AttachmentCompletionAcknowledgementProtocol.ValidateAgainstManifest(manifest, acknowledgement);

            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM attachment_sends
                WHERE sender_peer_id = $sender
                  AND recipient_peer_id = $recipient
                  AND attachment_id = $attachment_id;
                """;
            AddScopeParameters(command, senderPeerId, recipientPeerId, acknowledgement.AttachmentId);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private async Task<bool> RecordAttemptAsync(
        string senderPeerId,
        string recipientPeerId,
        string attachmentId,
        string? failureCode,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken)
    {
        ValidatePeerPair(senderPeerId, recipientPeerId);
        ValidateAttachmentId(attachmentId);
        await _localStore.InitializeAsync(cancellationToken);

        byte[]? protectedFailure = null;
        byte[]? key = null;
        try
        {
            if (failureCode is not null)
            {
                key = await GetKeyAsync(cancellationToken);
                protectedFailure = ProtectText(
                    key,
                    failureCode,
                    FailureContext(senderPeerId, recipientPeerId, attachmentId));
            }

            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE attachment_sends
                SET attempts = attempts + 1,
                    last_failure = $last_failure,
                    next_attempt_utc = $next_attempt,
                    updated_utc = $updated
                WHERE sender_peer_id = $sender
                  AND recipient_peer_id = $recipient
                  AND attachment_id = $attachment_id;
                """;
            AddScopeParameters(command, senderPeerId, recipientPeerId, attachmentId);
            command.Parameters.Add("$last_failure", SqliteType.Blob).Value = (object?)protectedFailure ?? DBNull.Value;
            command.Parameters.AddWithValue("$next_attempt", nextAttemptAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        finally
        {
            if (protectedFailure is not null)
            {
                CryptographicOperations.ZeroMemory(protectedFailure);
            }

            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    private async Task<AttachmentManifest?> ReadManifestAsync(
        string senderPeerId,
        string recipientPeerId,
        string attachmentId,
        byte[] key,
        CancellationToken cancellationToken)
    {
        ValidateAttachmentId(attachmentId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT file_name, content_type, size_bytes, chunk_size, sha256,
                   attempts, manifest_acknowledged
            FROM attachment_sends
            WHERE sender_peer_id = $sender
              AND recipient_peer_id = $recipient
              AND attachment_id = $attachment_id;
            """;
        AddScopeParameters(command, senderPeerId, recipientPeerId, attachmentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var row = new SendRow(
            senderPeerId,
            recipientPeerId,
            attachmentId,
            ((byte[])reader[0]).ToArray(),
            ((byte[])reader[1]).ToArray(),
            reader.GetInt64(2),
            reader.GetInt32(3),
            ((byte[])reader[4]).ToArray(),
            reader.GetInt32(5),
            reader.GetInt32(6) != 0);
        try
        {
            return DecryptManifest(key, row);
        }
        finally
        {
            row.Clear();
        }
    }

    private async Task<IReadOnlyList<AttachmentChunk>> ReadPendingChunksAsync(
        SqliteConnection connection,
        byte[] key,
        string senderPeerId,
        string recipientPeerId,
        AttachmentManifest manifest,
        int limit,
        CancellationToken cancellationToken)
    {
        var chunks = new List<AttachmentChunk>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT chunk_index, payload, payload_length
            FROM attachment_send_chunks
            WHERE sender_peer_id = $sender
              AND recipient_peer_id = $recipient
              AND attachment_id = $attachment_id
              AND acknowledged = 0
            ORDER BY chunk_index
            LIMIT $limit;
            """;
        AddScopeParameters(command, senderPeerId, recipientPeerId, manifest.AttachmentId);
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var chunkIndex = reader.GetInt32(0);
            var payload = Unprotect(
                key,
                (byte[])reader[1],
                ChunkContext(senderPeerId, recipientPeerId, manifest.AttachmentId, chunkIndex));
            if (payload.Length != reader.GetInt32(2))
            {
                CryptographicOperations.ZeroMemory(payload);
                throw new InvalidDataException("Stored attachment send chunk length metadata is inconsistent.");
            }

            var chunk = AttachmentProtocol.CreateChunk(manifest, chunkIndex, payload);
            CryptographicOperations.ZeroMemory(payload);
            chunks.Add(chunk);
        }

        return chunks;
    }

    private AttachmentManifest DecryptManifest(byte[] key, SendRow row)
    {
        var fileName = Unprotect(
            key,
            row.FileName,
            ManifestContext(row.SenderPeerId, row.RecipientPeerId, row.AttachmentId, "file-name"));
        var contentType = Unprotect(
            key,
            row.ContentType,
            ManifestContext(row.SenderPeerId, row.RecipientPeerId, row.AttachmentId, "content-type"));
        var sha256 = Unprotect(
            key,
            row.Sha256,
            ManifestContext(row.SenderPeerId, row.RecipientPeerId, row.AttachmentId, "sha256"));
        try
        {
            var manifest = new AttachmentManifest(
                AttachmentProtocol.CurrentVersion,
                row.AttachmentId,
                Encoding.UTF8.GetString(fileName),
                Encoding.UTF8.GetString(contentType),
                row.SizeBytes,
                row.ChunkSize,
                Convert.ToHexString(sha256).ToLowerInvariant());
            AttachmentProtocol.ValidateManifest(manifest);
            return manifest;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileName);
            CryptographicOperations.ZeroMemory(contentType);
            CryptographicOperations.ZeroMemory(sha256);
        }
    }

    private static async Task<bool> SendExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string senderPeerId,
        string recipientPeerId,
        string attachmentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM attachment_sends
            WHERE sender_peer_id = $sender
              AND recipient_peer_id = $recipient
              AND attachment_id = $attachment_id;
            """;
        AddScopeParameters(command, senderPeerId, recipientPeerId, attachmentId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private static async Task InsertChunkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string senderPeerId,
        string recipientPeerId,
        string attachmentId,
        AttachmentChunk chunk,
        byte[] protectedPayload,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO attachment_send_chunks(
                sender_peer_id, recipient_peer_id, attachment_id,
                chunk_index, payload, payload_length, acknowledged)
            VALUES($sender, $recipient, $attachment_id, $chunk_index, $payload, $payload_length, 0);
            """;
        AddScopeParameters(command, senderPeerId, recipientPeerId, attachmentId);
        command.Parameters.AddWithValue("$chunk_index", chunk.ChunkIndex);
        command.Parameters.Add("$payload", SqliteType.Blob).Value = protectedPayload;
        command.Parameters.AddWithValue("$payload_length", chunk.Data.Length);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSendAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string senderPeerId,
        string recipientPeerId,
        AttachmentManifest manifest,
        byte[] encryptedFileName,
        byte[] encryptedContentType,
        byte[] encryptedSha256,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO attachment_sends(
                sender_peer_id, recipient_peer_id, attachment_id,
                file_name, content_type, size_bytes, chunk_size, sha256,
                created_utc, updated_utc, next_attempt_utc, attempts,
                manifest_acknowledged, last_failure)
            VALUES(
                $sender, $recipient, $attachment_id,
                $file_name, $content_type, $size_bytes, $chunk_size, $sha256,
                $now, $now, $now, 0, 0, NULL);
            """;
        AddScopeParameters(command, senderPeerId, recipientPeerId, manifest.AttachmentId);
        command.Parameters.Add("$file_name", SqliteType.Blob).Value = encryptedFileName;
        command.Parameters.Add("$content_type", SqliteType.Blob).Value = encryptedContentType;
        command.Parameters.AddWithValue("$size_bytes", manifest.SizeBytes);
        command.Parameters.AddWithValue("$chunk_size", manifest.ChunkSize);
        command.Parameters.Add("$sha256", SqliteType.Blob).Value = encryptedSha256;
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<byte[]> GetKeyAsync(CancellationToken cancellationToken)
    {
        var key = await _keyProvider.GetOrCreateKeyAsync(cancellationToken);
        if (key.Length != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException("Dyract attachment send storage requires a 256-bit encryption key.");
        }

        return key;
    }

    private static byte[] ProtectText(byte[] key, string value, string context)
    {
        var plaintext = Encoding.UTF8.GetBytes(value);
        try
        {
            return Protect(key, plaintext, context);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
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
            throw new InvalidDataException("Stored attachment send value has an unsupported encrypted format.");
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

    private static void AddScopeParameters(
        SqliteCommand command,
        string senderPeerId,
        string recipientPeerId,
        string attachmentId)
    {
        command.Parameters.AddWithValue("$sender", senderPeerId);
        command.Parameters.AddWithValue("$recipient", recipientPeerId);
        command.Parameters.AddWithValue("$attachment_id", attachmentId);
    }

    private static void ValidatePeerPair(string senderPeerId, string recipientPeerId)
    {
        if (!PeerId.TryParse(senderPeerId, out var sender) ||
            !PeerId.TryParse(recipientPeerId, out var recipient) ||
            sender == recipient)
        {
            throw new ArgumentException("Attachment sender/recipient PeerId scope is invalid.");
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

    private static void ValidateFailureCode(string failureCode)
    {
        if (string.IsNullOrWhiteSpace(failureCode) ||
            failureCode.Length > MaximumFailureCodeLength ||
            failureCode.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ':' and not '.' and not '_' and not '-'))
        {
            throw new ArgumentException("Attachment send failure code must be a bounded diagnostic token.", nameof(failureCode));
        }
    }

    private static string ManifestContext(
        string senderPeerId,
        string recipientPeerId,
        string attachmentId,
        string field)
        => $"dyract:v1:attachment-send:{senderPeerId}:{recipientPeerId}:{attachmentId}:{field}";

    private static string ChunkContext(
        string senderPeerId,
        string recipientPeerId,
        string attachmentId,
        int chunkIndex)
        => $"dyract:v1:attachment-send:{senderPeerId}:{recipientPeerId}:{attachmentId}:chunk:{chunkIndex}";

    private static string FailureContext(string senderPeerId, string recipientPeerId, string attachmentId)
        => $"dyract:v1:attachment-send:{senderPeerId}:{recipientPeerId}:{attachmentId}:failure";

    private sealed class SendRow(
        string senderPeerId,
        string recipientPeerId,
        string attachmentId,
        byte[] fileName,
        byte[] contentType,
        long sizeBytes,
        int chunkSize,
        byte[] sha256,
        int attempts,
        bool manifestAcknowledged)
    {
        public string SenderPeerId { get; } = senderPeerId;
        public string RecipientPeerId { get; } = recipientPeerId;
        public string AttachmentId { get; } = attachmentId;
        public byte[] FileName { get; } = fileName;
        public byte[] ContentType { get; } = contentType;
        public long SizeBytes { get; } = sizeBytes;
        public int ChunkSize { get; } = chunkSize;
        public byte[] Sha256 { get; } = sha256;
        public int Attempts { get; } = attempts;
        public bool ManifestAcknowledged { get; } = manifestAcknowledged;

        public void Clear()
        {
            CryptographicOperations.ZeroMemory(FileName);
            CryptographicOperations.ZeroMemory(ContentType);
            CryptographicOperations.ZeroMemory(Sha256);
        }
    }
}
