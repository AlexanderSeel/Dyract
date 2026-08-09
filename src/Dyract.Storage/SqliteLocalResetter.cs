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
        using var transaction = connection.BeginTransaction();

        // Delete explicitly in dependency order. The schema/migration ledger is intentionally
        // retained so existing singleton stores remain valid after the key rotation.
        await ExecuteAsync(connection, transaction, "DELETE FROM outbox;", cancellationToken);
        await ExecuteAsync(connection, transaction, "DELETE FROM messages;", cancellationToken);
        await ExecuteAsync(connection, transaction, "DELETE FROM conversations;", cancellationToken);
        await ExecuteAsync(connection, transaction, "DELETE FROM contacts;", cancellationToken);

        await transaction.CommitAsync(cancellationToken);
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
}
