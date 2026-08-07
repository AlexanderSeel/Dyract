using System.Security.Cryptography;
using System.Text;
using Dyract.Core.Identity;
using Microsoft.Data.Sqlite;

namespace Dyract.Storage;

public sealed class SqliteIncomingMessageStore : IIncomingMessageStore
{
    private const int EncryptionNonceSize = 12;
    private const int EncryptionTagSize = 16;
    private const byte EncryptionFormatVersion = 1;
    private const int MaximumTextLength = 32_768;

    private readonly string _connectionString;
    private readonly ILocalEncryptionKeyProvider _keyProvider;
    private readonly ILocalStore _localStore;

    public SqliteIncomingMessageStore(
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

    public async Task<IncomingMessageStoreResult> StoreIncomingTextAsync(
        string messageId,
        string senderPeerId,
        string recipientPeerId,
        string text,
        DateTimeOffset createdAt,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateMessageId(messageId);
        if (!PeerId.TryParse(senderPeerId, out var sender))
        {
            throw new ArgumentException("Sender PeerId is invalid.", nameof(senderPeerId));
        }

        if (!PeerId.TryParse(recipientPeerId, out var recipient))
        {
            throw new ArgumentException("Recipient PeerId is invalid.", nameof(recipientPeerId));
        }

        if (sender == recipient)
        {
            throw new ArgumentException("Sender and recipient PeerIds must differ.", nameof(recipientPeerId));
        }

        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength)
        {
            throw new ArgumentException($"Text messages must contain 1-{MaximumTextLength} characters.", nameof(text));
        }

        var createdUnix = createdAt.ToUnixTimeMilliseconds();
        var receivedUnix = receivedAt.ToUnixTimeMilliseconds();
        if (receivedUnix < createdUnix - TimeSpan.FromDays(3650).TotalMilliseconds)
        {
            throw new ArgumentException("Received timestamp is implausibly older than the message timestamp.", nameof(receivedAt));
        }

        await _localStore.InitializeAsync(cancellationToken);
        var encryptedPayload = await ProtectTextAsync(
            text,
            MessagePayloadContext(messageId),
            cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await EnsureSenderContactExistsAsync(connection, transaction, sender.Value, cancellationToken);
        var conversationId = await GetOrCreateConversationIdAsync(
            connection,
            transaction,
            sender.Value,
            receivedUnix,
            cancellationToken);

        var inserted = await InsertIncomingMessageAsync(
            connection,
            transaction,
            messageId,
            conversationId,
            sender.Value,
            recipient.Value,
            createdUnix,
            receivedUnix,
            encryptedPayload,
            cancellationToken);

        if (inserted)
        {
            await TouchConversationAsync(
                connection,
                transaction,
                conversationId,
                receivedUnix,
                cancellationToken);
            transaction.Commit();

            return new IncomingMessageStoreResult(
                new LocalMessage(
                    messageId,
                    conversationId,
                    sender.Value,
                    recipient.Value,
                    LocalMessageDirection.Incoming,
                    LocalMessageType.Text,
                    LocalMessageState.Delivered,
                    DateTimeOffset.FromUnixTimeMilliseconds(createdUnix),
                    DateTimeOffset.FromUnixTimeMilliseconds(receivedUnix),
                    null,
                    text),
                true);
        }

        var existing = await ReadExistingMessageAsync(
            connection,
            transaction,
            messageId,
            cancellationToken);

        if (existing is null ||
            !string.Equals(existing.SenderPeerId, sender.Value, StringComparison.Ordinal) ||
            !string.Equals(existing.RecipientPeerId, recipient.Value, StringComparison.Ordinal) ||
            existing.Direction != LocalMessageDirection.Incoming ||
            existing.Type != LocalMessageType.Text ||
            existing.CreatedAt.ToUnixTimeMilliseconds() != createdUnix ||
            !string.Equals(existing.Text, text, StringComparison.Ordinal))
        {
            transaction.Rollback();
            throw new InvalidOperationException(
                "Message ID already exists with different content or direction.");
        }

        transaction.Commit();
        return new IncomingMessageStoreResult(existing, false);
    }

    private static void ValidateMessageId(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        if (messageId.Length != 32 ||
            !messageId.All(Uri.IsHexDigit) ||
            !string.Equals(messageId, messageId.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Message ID must be a lowercase 128-bit hexadecimal identifier.",
                nameof(messageId));
        }
    }

    private static async Task EnsureSenderContactExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string senderPeerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM contacts WHERE peer_id = $peer_id LIMIT 1;";
        command.Parameters.AddWithValue("$peer_id", senderPeerId);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new InvalidOperationException("Incoming messages are accepted only from a saved contact.");
        }
    }

    private static async Task<string> GetOrCreateConversationIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string senderPeerId,
        long receivedUnix,
        CancellationToken cancellationToken)
    {
        var proposedId = Guid.CreateVersion7().ToString("N");
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO conversations(conversation_id, peer_id, created_utc, last_activity_utc)
                VALUES($conversation_id, $peer_id, $created_utc, $last_activity_utc);
                """;
            insert.Parameters.AddWithValue("$conversation_id", proposedId);
            insert.Parameters.AddWithValue("$peer_id", senderPeerId);
            insert.Parameters.AddWithValue("$created_utc", receivedUnix);
            insert.Parameters.AddWithValue("$last_activity_utc", receivedUnix);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT conversation_id FROM conversations WHERE peer_id = $peer_id;";
        select.Parameters.AddWithValue("$peer_id", senderPeerId);
        return (string?)await select.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Incoming conversation could not be created.");
    }

    private static async Task<bool> InsertIncomingMessageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string messageId,
        string conversationId,
        string senderPeerId,
        string recipientPeerId,
        long createdUnix,
        long receivedUnix,
        byte[] encryptedPayload,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO messages(
                message_id, conversation_id, sender_peer_id, recipient_peer_id,
                direction, message_type, state, created_utc, delivered_utc, payload)
            VALUES(
                $message_id, $conversation_id, $sender_peer_id, $recipient_peer_id,
                $direction, $message_type, $state, $created_utc, $delivered_utc, $payload);
            """;
        command.Parameters.AddWithValue("$message_id", messageId);
        command.Parameters.AddWithValue("$conversation_id", conversationId);
        command.Parameters.AddWithValue("$sender_peer_id", senderPeerId);
        command.Parameters.AddWithValue("$recipient_peer_id", recipientPeerId);
        command.Parameters.AddWithValue("$direction", (int)LocalMessageDirection.Incoming);
        command.Parameters.AddWithValue("$message_type", (int)LocalMessageType.Text);
        command.Parameters.AddWithValue("$state", (int)LocalMessageState.Delivered);
        command.Parameters.AddWithValue("$created_utc", createdUnix);
        command.Parameters.AddWithValue("$delivered_utc", receivedUnix);
        command.Parameters.Add("$payload", SqliteType.Blob).Value = encryptedPayload;
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task TouchConversationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string conversationId,
        long receivedUnix,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE conversations
            SET last_activity_utc = CASE
                WHEN last_activity_utc < $received_utc THEN $received_utc
                ELSE last_activity_utc
            END
            WHERE conversation_id = $conversation_id;
            """;
        command.Parameters.AddWithValue("$received_utc", receivedUnix);
        command.Parameters.AddWithValue("$conversation_id", conversationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<LocalMessage?> ReadExistingMessageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string messageId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT conversation_id, sender_peer_id, recipient_peer_id,
                   direction, message_type, state, created_utc, delivered_utc, read_utc, payload
            FROM messages
            WHERE message_id = $message_id;
            """;
        command.Parameters.AddWithValue("$message_id", messageId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LocalMessage(
            messageId,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            (LocalMessageDirection)reader.GetInt32(3),
            (LocalMessageType)reader.GetInt32(4),
            (LocalMessageState)reader.GetInt32(5),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
            reader.IsDBNull(7) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
            reader.IsDBNull(8) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
            await UnprotectTextAsync(
                (byte[])reader[9],
                MessagePayloadContext(messageId),
                cancellationToken));
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
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

    private async ValueTask<byte[]> ProtectTextAsync(
        string value,
        string context,
        CancellationToken cancellationToken)
    {
        var key = await GetEncryptionKeyAsync(cancellationToken);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var nonce = RandomNumberGenerator.GetBytes(EncryptionNonceSize);
        var tag = new byte[EncryptionTagSize];
        var ciphertext = new byte[plaintext.Length];
        var associatedData = Encoding.UTF8.GetBytes(context);

        using var aes = new AesGcm(key, EncryptionTagSize);
        try
        {
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
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    private async ValueTask<string> UnprotectTextAsync(
        byte[] protectedValue,
        string context,
        CancellationToken cancellationToken)
    {
        if (protectedValue.Length < 1 + EncryptionNonceSize + EncryptionTagSize ||
            protectedValue[0] != EncryptionFormatVersion)
        {
            throw new CryptographicException("Local encrypted value has an unsupported format.");
        }

        var key = await GetEncryptionKeyAsync(cancellationToken);
        var nonce = protectedValue.AsSpan(1, EncryptionNonceSize);
        var tag = protectedValue.AsSpan(1 + EncryptionNonceSize, EncryptionTagSize);
        var ciphertext = protectedValue.AsSpan(1 + EncryptionNonceSize + EncryptionTagSize);
        var plaintext = new byte[ciphertext.Length];
        var associatedData = Encoding.UTF8.GetBytes(context);

        using var aes = new AesGcm(key, EncryptionTagSize);
        try
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    private async ValueTask<byte[]> GetEncryptionKeyAsync(CancellationToken cancellationToken)
    {
        var key = await _keyProvider.GetOrCreateKeyAsync(cancellationToken);
        if (key.Length != 32)
        {
            throw new CryptographicException("Dyract local storage requires a 256-bit encryption key.");
        }

        return key;
    }

    private static string MessagePayloadContext(string messageId)
        => $"dyract:v1:message:{messageId}:payload";
}
