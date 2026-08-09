using System.Security.Cryptography;
using System.Text;
using Dyract.Core.Identity;
using Microsoft.Data.Sqlite;

namespace Dyract.Storage;

public sealed class SqliteLocalStore : ILocalStore
{
    private const int EncryptionNonceSize = 12;
    private const int EncryptionTagSize = 16;
    private const byte EncryptionFormatVersion = 1;
    private const int MaximumTextLength = 32_768;

    private readonly string _connectionString;
    private readonly ILocalEncryptionKeyProvider _keyProvider;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private volatile bool _initialized;

    public SqliteLocalStore(string databasePath, ILocalEncryptionKeyProvider keyProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken);
            await ExecuteNonQueryAsync(connection, "PRAGMA synchronous=NORMAL;", cancellationToken);
            await ExecuteNonQueryAsync(connection, "PRAGMA busy_timeout=5000;", cancellationToken);

            const string schema = """
                CREATE TABLE IF NOT EXISTS schema_info (
                    version INTEGER NOT NULL
                );

                INSERT INTO schema_info(version)
                SELECT 1
                WHERE NOT EXISTS (SELECT 1 FROM schema_info);

                CREATE TABLE IF NOT EXISTS contacts (
                    peer_id TEXT PRIMARY KEY,
                    public_key BLOB NOT NULL,
                    display_name BLOB NOT NULL,
                    capability BLOB NULL,
                    added_utc INTEGER NOT NULL,
                    updated_utc INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS conversations (
                    conversation_id TEXT PRIMARY KEY,
                    peer_id TEXT NOT NULL UNIQUE,
                    created_utc INTEGER NOT NULL,
                    last_activity_utc INTEGER NOT NULL,
                    FOREIGN KEY(peer_id) REFERENCES contacts(peer_id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS messages (
                    message_id TEXT PRIMARY KEY,
                    conversation_id TEXT NOT NULL,
                    sender_peer_id TEXT NOT NULL,
                    recipient_peer_id TEXT NOT NULL,
                    direction INTEGER NOT NULL,
                    message_type INTEGER NOT NULL,
                    state INTEGER NOT NULL,
                    created_utc INTEGER NOT NULL,
                    delivered_utc INTEGER NULL,
                    read_utc INTEGER NULL,
                    payload BLOB NOT NULL,
                    FOREIGN KEY(conversation_id) REFERENCES conversations(conversation_id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS ix_messages_conversation_created
                    ON messages(conversation_id, created_utc, message_id);

                CREATE TABLE IF NOT EXISTS outbox (
                    message_id TEXT PRIMARY KEY,
                    next_attempt_utc INTEGER NOT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0,
                    last_error BLOB NULL,
                    FOREIGN KEY(message_id) REFERENCES messages(message_id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS ix_outbox_next_attempt
                    ON outbox(next_attempt_utc, message_id);
                """;

            await ExecuteNonQueryAsync(connection, schema, cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task UpsertContactAsync(ContactDraft contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contact);
        await EnsureInitializedAsync(cancellationToken);

        if (!PeerId.TryParse(contact.PeerId, out var peerId))
        {
            throw new ArgumentException("Contact PeerId is invalid.", nameof(contact));
        }

        if (contact.IdentityPublicKey is not { Length: > 0 } || PeerId.FromPublicKey(contact.IdentityPublicKey) != peerId)
        {
            throw new ArgumentException("Contact public key does not match its PeerId.", nameof(contact));
        }

        var displayName = contact.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 128)
        {
            throw new ArgumentException("Display name must contain 1-128 characters.", nameof(contact));
        }

        if (contact.Capability is { Length: > 32_768 })
        {
            throw new ArgumentException("Contact capability is too large.", nameof(contact));
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var encryptedName = await ProtectTextAsync(displayName, ContactNameContext(peerId.Value), cancellationToken);
        var encryptedCapability = contact.Capability is null
            ? null
            : await ProtectTextAsync(contact.Capability, ContactCapabilityContext(peerId.Value), cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO contacts(peer_id, public_key, display_name, capability, added_utc, updated_utc)
            VALUES($peer_id, $public_key, $display_name, $capability, $now, $now)
            ON CONFLICT(peer_id) DO UPDATE SET
                public_key = excluded.public_key,
                display_name = excluded.display_name,
                capability = excluded.capability,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$peer_id", peerId.Value);
        command.Parameters.Add("$public_key", SqliteType.Blob).Value = contact.IdentityPublicKey;
        command.Parameters.Add("$display_name", SqliteType.Blob).Value = encryptedName;
        command.Parameters.Add("$capability", SqliteType.Blob).Value = (object?)encryptedCapability ?? DBNull.Value;
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LocalContact>> GetContactsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var contacts = new List<LocalContact>();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT peer_id, public_key, display_name, capability, added_utc, updated_utc
            FROM contacts
            ORDER BY updated_utc DESC, peer_id ASC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            contacts.Add(await ReadContactAsync(reader, cancellationToken));
        }

        return contacts;
    }

    public async Task<LocalContact?> GetContactAsync(string peerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT peer_id, public_key, display_name, capability, added_utc, updated_utc
            FROM contacts
            WHERE peer_id = $peer_id;
            """;
        command.Parameters.AddWithValue("$peer_id", peerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? await ReadContactAsync(reader, cancellationToken)
            : null;
    }

    public async Task<LocalConversation> GetOrCreateConversationAsync(
        string peerId,
        CancellationToken cancellationToken = default)
    {
        if (!PeerId.TryParse(peerId, out var parsedPeerId))
        {
            throw new ArgumentException("PeerId is invalid.", nameof(peerId));
        }

        await EnsureInitializedAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var proposedId = Guid.CreateVersion7().ToString("N");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT OR IGNORE INTO conversations(conversation_id, peer_id, created_utc, last_activity_utc)
                VALUES($conversation_id, $peer_id, $created_utc, $last_activity_utc);
                """;
            insert.Parameters.AddWithValue("$conversation_id", proposedId);
            insert.Parameters.AddWithValue("$peer_id", parsedPeerId.Value);
            insert.Parameters.AddWithValue("$created_utc", now.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$last_activity_utc", now.ToUnixTimeMilliseconds());
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT conversation_id, peer_id, created_utc, last_activity_utc
            FROM conversations
            WHERE peer_id = $peer_id;
            """;
        select.Parameters.AddWithValue("$peer_id", parsedPeerId.Value);

        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("A conversation can only be created for a saved contact.");
        }

        return ReadConversation(reader);
    }

    public async Task<LocalMessage> QueueOutgoingTextAsync(
        string conversationId,
        string senderPeerId,
        string recipientPeerId,
        string text,
        DateTimeOffset? createdAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        if (!PeerId.TryParse(senderPeerId, out var sender))
        {
            throw new ArgumentException("Sender PeerId is invalid.", nameof(senderPeerId));
        }

        if (!PeerId.TryParse(recipientPeerId, out var recipient))
        {
            throw new ArgumentException("Recipient PeerId is invalid.", nameof(recipientPeerId));
        }

        var normalizedText = text?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedText) || normalizedText.Length > MaximumTextLength)
        {
            throw new ArgumentException($"Text messages must contain 1-{MaximumTextLength} characters.", nameof(text));
        }

        await EnsureInitializedAsync(cancellationToken);
        var created = createdAt ?? DateTimeOffset.UtcNow;
        var messageId = Guid.CreateVersion7().ToString("N");
        var encryptedPayload = await ProtectTextAsync(
            normalizedText,
            MessagePayloadContext(messageId),
            cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await using (var verify = connection.CreateCommand())
        {
            verify.Transaction = transaction;
            verify.CommandText = "SELECT peer_id FROM conversations WHERE conversation_id = $conversation_id;";
            verify.Parameters.AddWithValue("$conversation_id", conversationId);
            var conversationPeer = (string?)await verify.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(conversationPeer, recipient.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Conversation does not belong to the recipient peer.");
            }
        }

        await using (var insertMessage = connection.CreateCommand())
        {
            insertMessage.Transaction = transaction;
            insertMessage.CommandText = """
                INSERT INTO messages(
                    message_id, conversation_id, sender_peer_id, recipient_peer_id,
                    direction, message_type, state, created_utc, payload)
                VALUES(
                    $message_id, $conversation_id, $sender_peer_id, $recipient_peer_id,
                    $direction, $message_type, $state, $created_utc, $payload);
                """;
            insertMessage.Parameters.AddWithValue("$message_id", messageId);
            insertMessage.Parameters.AddWithValue("$conversation_id", conversationId);
            insertMessage.Parameters.AddWithValue("$sender_peer_id", sender.Value);
            insertMessage.Parameters.AddWithValue("$recipient_peer_id", recipient.Value);
            insertMessage.Parameters.AddWithValue("$direction", (int)LocalMessageDirection.Outgoing);
            insertMessage.Parameters.AddWithValue("$message_type", (int)LocalMessageType.Text);
            insertMessage.Parameters.AddWithValue("$state", (int)LocalMessageState.Queued);
            insertMessage.Parameters.AddWithValue("$created_utc", created.ToUnixTimeMilliseconds());
            insertMessage.Parameters.Add("$payload", SqliteType.Blob).Value = encryptedPayload;
            await insertMessage.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertOutbox = connection.CreateCommand())
        {
            insertOutbox.Transaction = transaction;
            insertOutbox.CommandText = """
                INSERT INTO outbox(message_id, next_attempt_utc, attempts)
                VALUES($message_id, $next_attempt_utc, 0);
                """;
            insertOutbox.Parameters.AddWithValue("$message_id", messageId);
            insertOutbox.Parameters.AddWithValue("$next_attempt_utc", created.ToUnixTimeMilliseconds());
            await insertOutbox.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var updateConversation = connection.CreateCommand())
        {
            updateConversation.Transaction = transaction;
            updateConversation.CommandText = """
                UPDATE conversations
                SET last_activity_utc = $last_activity_utc
                WHERE conversation_id = $conversation_id;
                """;
            updateConversation.Parameters.AddWithValue("$last_activity_utc", created.ToUnixTimeMilliseconds());
            updateConversation.Parameters.AddWithValue("$conversation_id", conversationId);
            await updateConversation.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();

        return new LocalMessage(
            messageId,
            conversationId,
            sender.Value,
            recipient.Value,
            LocalMessageDirection.Outgoing,
            LocalMessageType.Text,
            LocalMessageState.Queued,
            created,
            null,
            null,
            normalizedText);
    }

    public async Task<IReadOnlyList<LocalMessage>> GetMessagesAsync(
        string conversationId,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Message limit must be between 1 and 1000.");
        }

        await EnsureInitializedAsync(cancellationToken);
        var messages = new List<LocalMessage>();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id, conversation_id, sender_peer_id, recipient_peer_id,
                   direction, message_type, state, created_utc, delivered_utc, read_utc, payload
            FROM (
                SELECT message_id, conversation_id, sender_peer_id, recipient_peer_id,
                       direction, message_type, state, created_utc, delivered_utc, read_utc, payload
                FROM messages
                WHERE conversation_id = $conversation_id
                ORDER BY
                    CASE
                        WHEN direction = 0 THEN COALESCE(delivered_utc, created_utc)
                        ELSE created_utc
                    END DESC,
                    message_id DESC
                LIMIT $limit
            )
            ORDER BY
                CASE
                    WHEN direction = 0 THEN COALESCE(delivered_utc, created_utc)
                    ELSE created_utc
                END ASC,
                message_id ASC;
            """;
        command.Parameters.AddWithValue("$conversation_id", conversationId);
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var messageId = reader.GetString(0);
            var payload = (byte[])reader[10];
            var text = await UnprotectTextAsync(payload, MessagePayloadContext(messageId), cancellationToken);

            messages.Add(new LocalMessage(
                messageId,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                (LocalMessageDirection)reader.GetInt32(4),
                (LocalMessageType)reader.GetInt32(5),
                (LocalMessageState)reader.GetInt32(6),
                FromUnixMilliseconds(reader.GetInt64(7)),
                reader.IsDBNull(8) ? null : FromUnixMilliseconds(reader.GetInt64(8)),
                reader.IsDBNull(9) ? null : FromUnixMilliseconds(reader.GetInt64(9)),
                text));
        }

        return messages;
    }

    public async Task<IReadOnlyList<PendingOutboxItem>> GetPendingOutboxAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Outbox limit must be between 1 and 500.");
        }

        await EnsureInitializedAsync(cancellationToken);
        var items = new List<PendingOutboxItem>();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.message_id, m.conversation_id, m.recipient_peer_id, m.payload,
                   o.attempts, o.next_attempt_utc
            FROM outbox o
            INNER JOIN messages m ON m.message_id = o.message_id
            ORDER BY o.next_attempt_utc ASC, o.message_id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var messageId = reader.GetString(0);
            var payload = (byte[])reader[3];
            items.Add(new PendingOutboxItem(
                messageId,
                reader.GetString(1),
                reader.GetString(2),
                await UnprotectTextAsync(payload, MessagePayloadContext(messageId), cancellationToken),
                reader.GetInt32(4),
                FromUnixMilliseconds(reader.GetInt64(5))));
        }

        return items;
    }

    public async Task RecordOutboxFailureAsync(
        string messageId,
        string? error,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        await EnsureInitializedAsync(cancellationToken);

        byte[]? encryptedError = null;
        if (!string.IsNullOrWhiteSpace(error))
        {
            var safeError = error.Length <= 1024 ? error : error[..1024];
            encryptedError = await ProtectTextAsync(safeError, OutboxErrorContext(messageId), cancellationToken);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE outbox
            SET attempts = attempts + 1,
                next_attempt_utc = $next_attempt_utc,
                last_error = $last_error
            WHERE message_id = $message_id;
            """;
        command.Parameters.AddWithValue("$message_id", messageId);
        command.Parameters.AddWithValue("$next_attempt_utc", nextAttemptAt.ToUnixTimeMilliseconds());
        command.Parameters.Add("$last_error", SqliteType.Blob).Value = (object?)encryptedError ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkDeliveredAsync(
        string messageId,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE messages
                SET state = $state, delivered_utc = $delivered_utc
                WHERE message_id = $message_id;
                """;
            update.Parameters.AddWithValue("$state", (int)LocalMessageState.Delivered);
            update.Parameters.AddWithValue("$delivered_utc", deliveredAt.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$message_id", messageId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var remove = connection.CreateCommand())
        {
            remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM outbox WHERE message_id = $message_id;";
            remove.Parameters.AddWithValue("$message_id", messageId);
            await remove.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    private async Task<LocalContact> ReadContactAsync(SqliteDataReader reader, CancellationToken cancellationToken)
    {
        var peerId = reader.GetString(0);
        var displayName = await UnprotectTextAsync(
            (byte[])reader[2],
            ContactNameContext(peerId),
            cancellationToken);
        var capability = reader.IsDBNull(3)
            ? null
            : await UnprotectTextAsync(
                (byte[])reader[3],
                ContactCapabilityContext(peerId),
                cancellationToken);

        return new LocalContact(
            peerId,
            ((byte[])reader[1]).ToArray(),
            displayName,
            capability,
            FromUnixMilliseconds(reader.GetInt64(4)),
            FromUnixMilliseconds(reader.GetInt64(5)));
    }

    private static LocalConversation ReadConversation(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            FromUnixMilliseconds(reader.GetInt64(2)),
            FromUnixMilliseconds(reader.GetInt64(3)));

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken);
        }
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
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        var result = new byte[1 + nonce.Length + tag.Length + ciphertext.Length];
        result[0] = EncryptionFormatVersion;
        nonce.CopyTo(result, 1);
        tag.CopyTo(result, 1 + nonce.Length);
        ciphertext.CopyTo(result, 1 + nonce.Length + tag.Length);
        CryptographicOperations.ZeroMemory(plaintext);
        return result;
    }

    private async ValueTask<string> UnprotectTextAsync(
        byte[] protectedValue,
        string context,
        CancellationToken cancellationToken)
    {
        if (protectedValue.Length < 1 + EncryptionNonceSize + EncryptionTagSize || protectedValue[0] != EncryptionFormatVersion)
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
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);

        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
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

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTimeOffset FromUnixMilliseconds(long value)
        => DateTimeOffset.FromUnixTimeMilliseconds(value);

    private static string ContactNameContext(string peerId) => $"dyract:v1:contact:{peerId}:display-name";
    private static string ContactCapabilityContext(string peerId) => $"dyract:v1:contact:{peerId}:capability";
    private static string MessagePayloadContext(string messageId) => $"dyract:v1:message:{messageId}:payload";
    private static string OutboxErrorContext(string messageId) => $"dyract:v1:outbox:{messageId}:error";
}
