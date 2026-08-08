using Dyract.Core.Identity;
using Microsoft.Data.Sqlite;

namespace Dyract.Storage;

public sealed class SqliteReadReceiptStore : IIncomingReadStore, IOutgoingReadStore
{
    private readonly string _connectionString;
    private readonly ILocalStore _localStore;

    public SqliteReadReceiptStore(string databasePath, ILocalStore localStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _localStore = localStore ?? throw new ArgumentNullException(nameof(localStore));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public Task<bool> MarkIncomingReadAsync(
        string messageId,
        string readerPeerId,
        string senderPeerId,
        DateTimeOffset readAt,
        CancellationToken cancellationToken = default)
        => MarkReadAsync(
            messageId,
            expectedSenderPeerId: senderPeerId,
            expectedRecipientPeerId: readerPeerId,
            expectedDirection: LocalMessageDirection.Incoming,
            readAt,
            removeOutbox: false,
            cancellationToken);

    public Task<bool> MarkOutgoingReadAsync(
        string messageId,
        string senderPeerId,
        string readerPeerId,
        DateTimeOffset readAt,
        CancellationToken cancellationToken = default)
        => MarkReadAsync(
            messageId,
            expectedSenderPeerId: senderPeerId,
            expectedRecipientPeerId: readerPeerId,
            expectedDirection: LocalMessageDirection.Outgoing,
            readAt,
            removeOutbox: true,
            cancellationToken);

    private async Task<bool> MarkReadAsync(
        string messageId,
        string expectedSenderPeerId,
        string expectedRecipientPeerId,
        LocalMessageDirection expectedDirection,
        DateTimeOffset readAt,
        bool removeOutbox,
        CancellationToken cancellationToken)
    {
        ValidateMessageId(messageId);
        var (sender, recipient) = ValidatePeerPair(expectedSenderPeerId, expectedRecipientPeerId);
        var readUnix = readAt.ToUnixTimeMilliseconds();

        await _localStore.InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        LocalMessageDirection direction;
        string storedSender;
        string storedRecipient;

        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT sender_peer_id, recipient_peer_id, direction
                FROM messages
                WHERE message_id = $message_id;
                """;
            select.Parameters.AddWithValue("$message_id", messageId);

            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                transaction.Rollback();
                return false;
            }

            storedSender = reader.GetString(0);
            storedRecipient = reader.GetString(1);
            direction = (LocalMessageDirection)reader.GetInt32(2);
        }

        if (direction != expectedDirection ||
            !string.Equals(storedSender, sender.Value, StringComparison.Ordinal) ||
            !string.Equals(storedRecipient, recipient.Value, StringComparison.Ordinal))
        {
            transaction.Rollback();
            throw new InvalidOperationException(
                "Read receipt does not match the stored message peer scope and direction.");
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE messages
                SET state = $state,
                    delivered_utc = COALESCE(delivered_utc, $read_utc),
                    read_utc = COALESCE(read_utc, $read_utc)
                WHERE message_id = $message_id;
                """;
            update.Parameters.AddWithValue("$state", (int)LocalMessageState.Read);
            update.Parameters.AddWithValue("$read_utc", readUnix);
            update.Parameters.AddWithValue("$message_id", messageId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        if (removeOutbox)
        {
            await using var remove = connection.CreateCommand();
            remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM outbox WHERE message_id = $message_id;";
            remove.Parameters.AddWithValue("$message_id", messageId);
            await remove.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
        return true;
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
}
