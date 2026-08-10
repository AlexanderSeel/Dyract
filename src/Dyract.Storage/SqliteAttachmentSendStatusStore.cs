using System.Security.Cryptography;
using System.Text;
using Dyract.Core.Identity;
using Dyract.Protocol;
using Microsoft.Data.Sqlite;

namespace Dyract.Storage;

public sealed record AttachmentSendStatus(
    string SenderPeerId,
    string RecipientPeerId,
    AttachmentManifest Manifest,
    int Attempts,
    string? LastFailure,
    bool ManifestAcknowledged,
    int TotalChunks,
    int AcknowledgedChunks,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset NextAttemptAt)
{
    public int PendingChunks => Math.Max(0, TotalChunks - AcknowledgedChunks);
    public bool WaitingForCompletion => ManifestAcknowledged && PendingChunks == 0;
}

/// <summary>
/// Read-only, bounded sender-state projection for local UI. Sensitive manifest fields and failure
/// tokens are decrypted only after exact sender/recipient scope selection.
/// </summary>
public sealed class SqliteAttachmentSendStatusStore
{
    private const byte EncryptionFormatVersion = 1;
    private const int EncryptionNonceSize = 12;
    private const int EncryptionTagSize = 16;
    private const int MaximumStatusRows = 16;

    private readonly string _connectionString;
    private readonly ILocalEncryptionKeyProvider _keyProvider;
    private readonly ILocalStore _localStore;

    public SqliteAttachmentSendStatusStore(
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

    public async Task<IReadOnlyList<AttachmentSendStatus>> GetPendingAsync(
        string senderPeerId,
        string recipientPeerId,
        int limit = MaximumStatusRows,
        CancellationToken cancellationToken = default)
    {
        ValidatePeerPair(senderPeerId, recipientPeerId);
        if (limit is < 1 or > MaximumStatusRows)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await _localStore.InitializeAsync(cancellationToken);
        var key = await _keyProvider.GetOrCreateKeyAsync(cancellationToken);
        if (key.Length != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException("Dyract attachment sender status requires a 256-bit local encryption key.");
        }

        try
        {
            var result = new List<AttachmentSendStatus>();
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT s.attachment_id, s.file_name, s.content_type, s.size_bytes, s.chunk_size, s.sha256,
                       s.created_utc, s.updated_utc, s.next_attempt_utc, s.attempts,
                       s.manifest_acknowledged, s.last_failure,
                       (SELECT COUNT(*)
                        FROM attachment_send_chunks c
                        WHERE c.sender_peer_id = s.sender_peer_id
                          AND c.recipient_peer_id = s.recipient_peer_id
                          AND c.attachment_id = s.attachment_id) AS total_chunks,
                       (SELECT COUNT(*)
                        FROM attachment_send_chunks c
                        WHERE c.sender_peer_id = s.sender_peer_id
                          AND c.recipient_peer_id = s.recipient_peer_id
                          AND c.attachment_id = s.attachment_id
                          AND c.acknowledged <> 0) AS acknowledged_chunks
                FROM attachment_sends s
                WHERE s.sender_peer_id = $sender
                  AND s.recipient_peer_id = $recipient
                ORDER BY s.created_utc, s.attachment_id
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$sender", senderPeerId);
            command.Parameters.AddWithValue("$recipient", recipientPeerId);
            command.Parameters.AddWithValue("$limit", limit);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var attachmentId = reader.GetString(0);
                var fileName = UnprotectText(
                    key,
                    (byte[])reader[1],
                    ManifestContext(senderPeerId, recipientPeerId, attachmentId, "file-name"));
                var contentType = UnprotectText(
                    key,
                    (byte[])reader[2],
                    ManifestContext(senderPeerId, recipientPeerId, attachmentId, "content-type"));
                var sha256 = Unprotect(
                    key,
                    (byte[])reader[5],
                    ManifestContext(senderPeerId, recipientPeerId, attachmentId, "sha256"));
                string? lastFailure = null;
                if (!reader.IsDBNull(11))
                {
                    lastFailure = UnprotectText(
                        key,
                        (byte[])reader[11],
                        FailureContext(senderPeerId, recipientPeerId, attachmentId));
                }

                try
                {
                    var manifest = new AttachmentManifest(
                        AttachmentProtocol.CurrentVersion,
                        attachmentId,
                        fileName,
                        contentType,
                        reader.GetInt64(3),
                        reader.GetInt32(4),
                        Convert.ToHexString(sha256).ToLowerInvariant());
                    AttachmentProtocol.ValidateManifest(manifest);

                    var totalChunks = checked((int)reader.GetInt64(12));
                    var acknowledgedChunks = checked((int)reader.GetInt64(13));
                    if (totalChunks != manifest.ChunkCount || acknowledgedChunks < 0 || acknowledgedChunks > totalChunks)
                    {
                        throw new InvalidDataException("Stored attachment sender progress is inconsistent with its manifest.");
                    }

                    result.Add(new AttachmentSendStatus(
                        senderPeerId,
                        recipientPeerId,
                        manifest,
                        reader.GetInt32(9),
                        lastFailure,
                        reader.GetInt32(10) != 0,
                        totalChunks,
                        acknowledgedChunks,
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8))));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(sha256);
                }
            }

            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static string UnprotectText(byte[] key, byte[] protectedValue, string context)
    {
        var plaintext = Unprotect(key, protectedValue, context);
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] Unprotect(byte[] key, byte[] protectedValue, string context)
    {
        if (protectedValue.Length < 1 + EncryptionNonceSize + EncryptionTagSize ||
            protectedValue[0] != EncryptionFormatVersion)
        {
            throw new InvalidDataException("Stored attachment sender status has an unsupported encrypted format.");
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

    private static void ValidatePeerPair(string senderPeerId, string recipientPeerId)
    {
        if (!PeerId.TryParse(senderPeerId, out var sender) ||
            !PeerId.TryParse(recipientPeerId, out var recipient) ||
            sender == recipient)
        {
            throw new ArgumentException("Attachment sender/recipient PeerId scope is invalid.");
        }
    }

    private static string ManifestContext(
        string senderPeerId,
        string recipientPeerId,
        string attachmentId,
        string field)
        => $"dyract:v1:attachment-send:{senderPeerId}:{recipientPeerId}:{attachmentId}:{field}";

    private static string FailureContext(string senderPeerId, string recipientPeerId, string attachmentId)
        => $"dyract:v1:attachment-send:{senderPeerId}:{recipientPeerId}:{attachmentId}:failure";
}
