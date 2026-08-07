using System.Security.Cryptography;
using System.Text;
using Dyract.Core.Identity;
using Microsoft.Data.Sqlite;

namespace Dyract.Storage;

public sealed class SqliteOutboxDeliveryQueue : IOutboxDeliveryQueue
{
    private const int EncryptionNonceSize = 12;
    private const int EncryptionTagSize = 16;
    private const byte EncryptionFormatVersion = 1;
    private const int MaximumFailureCodeLength = 256;

    private readonly string _connectionString;
    private readonly ILocalEncryptionKeyProvider _keyProvider;
    private readonly ILocalStore _localStore;

    public SqliteOutboxDeliveryQueue(
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

    public async Task<IReadOnlyList<DueOutboxMessage>> GetDueOutboxAsync(
        DateTimeOffset dueAtOrBefore,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Outbox limit must be between 1 and 500.");
        }

        await _localStore.InitializeAsync(cancellationToken);
        var items = new List<DueOutboxMessage>();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.message_id, m.sender_peer_id, m.recipient_peer_id, m.created_utc,
                   m.payload, o.attempts, o.next_attempt_utc
            FROM outbox o
            INNER JOIN messages m ON m.message_id = o.message_id
            WHERE o.next_attempt_utc <= $due_utc
              AND m.direction = $direction
              AND m.message_type = $message_type
              AND m.state NOT IN ($delivered_state, $read_state)
            ORDER BY o.next_attempt_utc ASC, o.message_id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$due_utc", dueAtOrBefore.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$direction", (int)LocalMessageDirection.Outgoing);
        command.Parameters.AddWithValue("$message_type", (int)LocalMessageType.Text);
        command.Parameters.AddWithValue("$delivered_state", (int)LocalMessageState.Delivered);
        command.Parameters.AddWithValue("$read_state", (int)LocalMessageState.Read);
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var messageId = reader.GetString(0);
            var senderPeerId = reader.GetString(1);
            var recipientPeerId = reader.GetString(2);
            if (!PeerId.TryParse(senderPeerId, out _) || !PeerId.TryParse(recipientPeerId, out _))
            {
                throw new InvalidDataException("Outbox contains an invalid PeerId.");
            }

            items.Add(new DueOutboxMessage(
                messageId,
                senderPeerId,
                recipientPeerId,
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                await UnprotectTextAsync(
                    (byte[])reader[4],
                    MessagePayloadContext(messageId),
                    cancellationToken),
                reader.GetInt32(5),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6))));
        }

        return items;
    }

    public Task<bool> RecordOutboundSentAsync(
        string messageId,
        string senderPeerId,
        string recipientPeerId,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default)
        => RecordAttemptAsync(
            messageId,
            senderPeerId,
            recipientPeerId,
            LocalMessageState.Sent,
            failureCode: null,
            nextAttemptAt,
            cancellationToken);

    public Task<bool> RecordOutboundFailureAsync(
        string messageId,
        string senderPeerId,
        string recipientPeerId,
        string failureCode,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        var safeCode = failureCode.Length <= MaximumFailureCodeLength
            ? failureCode
            : failureCode[..MaximumFailureCodeLength];
        return RecordAttemptAsync(
            messageId,
            senderPeerId,
            recipientPeerId,
            LocalMessageState.Failed,
            safeCode,
            nextAttemptAt,
            cancellationToken);
    }

    private async Task<bool> RecordAttemptAsync(
        string messageId,
        string senderPeerId,
        string recipientPeerId,
        LocalMessageState targetState,
        string? failureCode,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken)
    {
        ValidateMessageId(messageId);
        var (sender, recipient) = ValidatePeerPair(senderPeerId, recipientPeerId);
        await _localStore.InitializeAsync(cancellationToken);

        byte[]? encryptedFailure = null;
        if (failureCode is not null)
        {
            encryptedFailure = await ProtectTextAsync(
                failureCode,
                OutboxErrorContext(messageId),
                cancellationToken);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        if (!await EnsureExactOutgoingOutboxAsync(
                connection,
                transaction,
                messageId,
                sender.Value,
                recipient.Value,
                cancellationToken))
        {
            transaction.Rollback();
            return false;
        }

        await using (var updateMessage = connection.CreateCommand())
        {
            updateMessage.Transaction = transaction;
            updateMessage.CommandText = """
                UPDATE messages
                SET state = $state
                WHERE message_id = $message_id
                  AND state NOT IN ($delivered_state, $read_state)
                  AND EXISTS(SELECT 1 FROM outbox WHERE message_id = $message_id);
                """;
            updateMessage.Parameters.AddWithValue("$state", (int)targetState);
            updateMessage.Parameters.AddWithValue("$delivered_state", (int)LocalMessageState.Delivered);
            updateMessage.Parameters.AddWithValue("$read_state", (int)LocalMessageState.Read);
            updateMessage.Parameters.AddWithValue("$message_id", messageId);
            await updateMessage.ExecuteNonQueryAsync(cancellationToken);
        }

        int updatedOutbox;
        await using (var updateOutbox = connection.CreateCommand())
        {
            updateOutbox.Transaction = transaction;
            updateOutbox.CommandText = """
                UPDATE outbox
                SET attempts = attempts + 1,
                    next_attempt_utc = $next_attempt_utc,
                    last_error = $last_error
                WHERE message_id = $message_id;
                """;
            updateOutbox.Parameters.AddWithValue("$next_attempt_utc", nextAttemptAt.ToUnixTimeMilliseconds());
            updateOutbox.Parameters.Add("$last_error", SqliteType.Blob).Value = (object?)encryptedFailure ?? DBNull.Value;
            updateOutbox.Parameters.AddWithValue("$message_id", messageId);
            updatedOutbox = await updateOutbox.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
        return updatedOutbox == 1;
    }

    private static async Task<bool> EnsureExactOutgoingOutboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string messageId,
        string senderPeerId,
        string recipientPeerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT m.sender_peer_id, m.recipient_peer_id, m.direction
            FROM outbox o
            INNER JOIN messages m ON m.message_id = o.message_id
            WHERE o.message_id = $message_id;
            """;
        command.Parameters.AddWithValue("$message_id", messageId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return false;
        }

        if (!string.Equals(reader.GetString(0), senderPeerId, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), recipientPeerId, StringComparison.Ordinal) ||
            (LocalMessageDirection)reader.GetInt32(2) != LocalMessageDirection.Outgoing)
        {
            throw new InvalidOperationException("Outbox attempt does not match the stored outgoing message peer scope.");
        }

        return true;
    }

    private static (PeerId Sender, PeerId Recipient) ValidatePeerPair(
        string senderPeerId,
        string recipientPeerId)
    {
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

        return (sender, recipient);
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

    private static string OutboxErrorContext(string messageId)
        => $"dyract:v1:outbox:{messageId}:error";
}
