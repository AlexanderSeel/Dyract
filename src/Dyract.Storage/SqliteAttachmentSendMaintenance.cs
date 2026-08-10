using Dyract.Core.Identity;
using Dyract.Protocol;
using Microsoft.Data.Sqlite;

namespace Dyract.Storage;

/// <summary>
/// Explicit maintenance actions for durable attachment sender snapshots.
/// Dyract deliberately has no time-based automatic sender expiry: a queued attachment remains
/// until verified DYAC completion, explicit user cancellation, or destructive identity reset.
/// </summary>
public sealed class SqliteAttachmentSendMaintenance
{
    private readonly string _connectionString;
    private readonly ILocalStore _localStore;

    public SqliteAttachmentSendMaintenance(string databasePath, ILocalStore localStore)
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

    public async Task<bool> CancelAsync(
        string senderPeerId,
        string recipientPeerId,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(senderPeerId, recipientPeerId, attachmentId);
        await _localStore.InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM attachment_sends
            WHERE sender_peer_id = $sender
              AND recipient_peer_id = $recipient
              AND attachment_id = $attachment_id;
            """;
        command.Parameters.AddWithValue("$sender", senderPeerId);
        command.Parameters.AddWithValue("$recipient", recipientPeerId);
        command.Parameters.AddWithValue("$attachment_id", attachmentId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static void ValidateScope(string senderPeerId, string recipientPeerId, string attachmentId)
    {
        if (!PeerId.TryParse(senderPeerId, out var sender) ||
            !PeerId.TryParse(recipientPeerId, out var recipient) ||
            sender == recipient)
        {
            throw new ArgumentException("Attachment sender/recipient PeerId scope is invalid.");
        }

        if (string.IsNullOrWhiteSpace(attachmentId) ||
            attachmentId.Length != AttachmentProtocol.AttachmentIdHexLength ||
            attachmentId.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("AttachmentId is invalid.", nameof(attachmentId));
        }
    }
}
