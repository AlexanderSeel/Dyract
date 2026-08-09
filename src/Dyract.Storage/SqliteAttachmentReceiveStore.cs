using System.Security.Cryptography;
using System.Text;
using Dyract.Core.Identity;
using Dyract.Protocol;
using Microsoft.Data.Sqlite;

namespace Dyract.Storage;

public enum AttachmentManifestStoreResult
{
    Created,
    Existing
}

public enum AttachmentChunkStoreResult
{
    Stored,
    Duplicate
}

public sealed class SqliteAttachmentReceiveStore
{
    private const byte EncryptionFormatVersion = 1;
    private const int EncryptionNonceSize = 12;
    private const int EncryptionTagSize = 16;

    private readonly string _connectionString;
    private readonly ILocalEncryptionKeyProvider _keyProvider;
    private readonly ILocalStore _localStore;

    public SqliteAttachmentReceiveStore(
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

    public async Task<AttachmentManifestStoreResult> StoreManifestAsync(
        string senderPeerId,
        AttachmentManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ValidatePeer(senderPeerId);
        AttachmentProtocol.ValidateManifest(manifest);
        await _localStore.InitializeAsync(cancellationToken);

        var existing = await GetManifestAsync(senderPeerId, manifest.AttachmentId, cancellationToken);
        if (existing is not null)
        {
            if (existing != manifest)
            {
                throw new InvalidDataException("AttachmentId collision contains different manifest content.");
            }

            return AttachmentManifestStoreResult.Existing;
        }

        var encryptedFileName = await ProtectAsync(
            Encoding.UTF8.GetBytes(manifest.FileName),
            ManifestContext(senderPeerId, manifest.AttachmentId, "file-name"),
            cancellationToken);
        var encryptedContentType = await ProtectAsync(
            Encoding.UTF8.GetBytes(manifest.ContentType),
            ManifestContext(senderPeerId, manifest.AttachmentId, "content-type"),
            cancellationToken);
        var encryptedSha256 = await ProtectAsync(
            Convert.FromHexString(manifest.Sha256),
            ManifestContext(senderPeerId, manifest.AttachmentId, "sha256"),
            cancellationToken);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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

        existing = await GetManifestAsync(senderPeerId, manifest.AttachmentId, cancellationToken);
        if (existing == manifest)
        {
            return AttachmentManifestStoreResult.Existing;
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

        var fileNameBytes = await UnprotectAsync(
            (byte[])reader[0],
            ManifestContext(senderPeerId, attachmentId, "file-name"),
            cancellationToken);
        var contentTypeBytes = await UnprotectAsync(
            (byte[])reader[1],
            ManifestContext(senderPeerId, attachmentId, "content-type"),
            cancellationToken);
        var sha256Bytes = await UnprotectAsync(
            (byte[])reader[4],
            ManifestContext(senderPeerId, attachmentId, "sha256"),
            cancellationToken);

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

    public async Task<AttachmentChunkStoreResult> StoreChunkAsync(
        string senderPeerId,
        AttachmentChunk chunk,
        CancellationToken cancellationToken = default)
    {
        ValidatePeer(senderPeerId);
        ArgumentNullException.ThrowIfNull(chunk);
        var manifest = await GetManifestAsync(senderPeerId, chunk.AttachmentId, cancellationToken)
            ?? throw new InvalidDataException("Attachment manifest must be stored before chunks are accepted.");
        AttachmentProtocol.ValidateChunk(manifest, chunk);

        var protectedPayload = await ProtectAsync(
            chunk.Data,
            ChunkContext(senderPeerId, chunk.AttachmentId, chunk.ChunkIndex),
            cancellationToken);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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

        var plaintext = await UnprotectAsync(
            (byte[])reader[0],
            ChunkContext(senderPeerId, attachmentId, chunkIndex),
            cancellationToken);
        if (plaintext.Length != reader.GetInt32(1))
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException("Stored attachment chunk length metadata is inconsistent.");
        }

        return plaintext;
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

    private async Task<byte[]> ProtectAsync(
        ReadOnlyMemory<byte> plaintext,
        string context,
        CancellationToken cancellationToken)
    {
        var key = await _keyProvider.GetOrCreateKeyAsync(cancellationToken);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(EncryptionNonceSize);
            var tag = new byte[EncryptionTagSize];
            var ciphertext = new byte[plaintext.Length];
            var associatedData = Encoding.UTF8.GetBytes(context);
            using var aes = new AesGcm(key, EncryptionTagSize);
            aes.Encrypt(nonce, plaintext.Span, ciphertext, tag, associatedData);

            var result = new byte[1 + nonce.Length + tag.Length + ciphertext.Length];
            result[0] = EncryptionFormatVersion;
            nonce.CopyTo(result, 1);
            tag.CopyTo(result, 1 + nonce.Length);
            ciphertext.CopyTo(result, 1 + nonce.Length + tag.Length);
            CryptographicOperations.ZeroMemory(ciphertext);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private async Task<byte[]> UnprotectAsync(
        byte[] protectedValue,
        string context,
        CancellationToken cancellationToken)
    {
        if (protectedValue.Length < 1 + EncryptionNonceSize + EncryptionTagSize ||
            protectedValue[0] != EncryptionFormatVersion)
        {
            throw new InvalidDataException("Stored attachment value has an unsupported encrypted format.");
        }

        var key = await _keyProvider.GetOrCreateKeyAsync(cancellationToken);
        var plaintext = new byte[protectedValue.Length - 1 - EncryptionNonceSize - EncryptionTagSize];
        try
        {
            var nonce = protectedValue.AsSpan(1, EncryptionNonceSize);
            var tag = protectedValue.AsSpan(1 + EncryptionNonceSize, EncryptionTagSize);
            var ciphertext = protectedValue.AsSpan(1 + EncryptionNonceSize + EncryptionTagSize);
            var associatedData = Encoding.UTF8.GetBytes(context);
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
            CryptographicOperations.ZeroMemory(key);
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

    private static void ValidatePeer(string senderPeerId)
    {
        if (!PeerId.TryParse(senderPeerId, out _))
        {
            throw new ArgumentException("Sender PeerId is invalid.", nameof(senderPeerId));
        }
    }

    private static void ValidateAttachmentId(string attachmentId)
    {
        if (attachmentId.Length != AttachmentProtocol.AttachmentIdHexLength ||
            attachmentId.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("AttachmentId is invalid.", nameof(attachmentId));
        }
    }

    private static string ManifestContext(string senderPeerId, string attachmentId, string field)
        => $"dyract:v1:attachment:{senderPeerId}:{attachmentId}:manifest:{field}";

    private static string ChunkContext(string senderPeerId, string attachmentId, int chunkIndex)
        => $"dyract:v1:attachment:{senderPeerId}:{attachmentId}:chunk:{chunkIndex}";
}
