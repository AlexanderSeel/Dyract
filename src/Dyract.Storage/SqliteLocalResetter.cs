using Microsoft.Data.Sqlite;

namespace Dyract.Storage;

/// <summary>
/// Removes identity-bound local user state while preserving the validated SQLite schema.
/// This allows a running app to rotate its SecureStorage keys without leaving already-created
/// store instances pointed at a deleted database file.
/// </summary>
public static class SqliteLocalResetter
{
    public static async Task ResetUserDataAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Best-effort reduction of deleted-row residue in SQLite pages. The subsequent key
        // rotation remains the confidentiality boundary; flash wear-leveling means an app
        // must not claim forensic secure erase of the physical device.
        await ExecuteConnectionAsync(connection, "PRAGMA secure_delete=ON;", cancellationToken);

        using (var transaction = connection.BeginTransaction())
        {
            // Attachment tables were introduced after the original local schema. A pending
            // reset from an older app version must still be completable before migrations run.
            if (await TableExistsAsync(connection, transaction, "attachment_receive_chunks", cancellationToken))
            {
                await ExecuteAsync(connection, transaction, "DELETE FROM attachment_receive_chunks;", cancellationToken);
            }

            if (await TableExistsAsync(connection, transaction, "attachment_receives", cancellationToken))
            {
                await ExecuteAsync(connection, transaction, "DELETE FROM attachment_receives;", cancellationToken);
            }

            // Delete core user state explicitly in dependency order. The schema/migration ledger
            // is intentionally retained so existing singleton stores remain valid after key rotation.
            await ExecuteAsync(connection, transaction, "DELETE FROM outbox;", cancellationToken);
            await ExecuteAsync(connection, transaction, "DELETE FROM messages;", cancellationToken);
            await ExecuteAsync(connection, transaction, "DELETE FROM conversations;", cancellationToken);
            await ExecuteAsync(connection, transaction, "DELETE FROM contacts;", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // Remove committed WAL history and compact free pages after the transactional wipe.
        await ExecuteConnectionAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
        await ExecuteConnectionAsync(connection, "VACUUM;", cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $table_name;";
        command.Parameters.AddWithValue("$table_name", tableName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteConnectionAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
