using Microsoft.Data.Sqlite;

namespace Dyract.Storage;

/// <summary>
/// Maintains an append-only migration ledger for the local SQLite database.
/// The original Dyract schema predates this runner and is adopted as migration 1.
/// Future schema changes must be added as ordered migration definitions here.
/// </summary>
public sealed class SqliteSchemaMigrationRunner
{
    public const int CurrentVersion = 5;

    private static readonly MigrationDefinition[] Migrations =
    [
        new(1, "baseline-v1", Sql: null),
        new(2, "track-issued-contact-capability", """
            ALTER TABLE contacts ADD COLUMN granted_capability BLOB NULL;
            """),
        new(3, "durable-attachment-receive-state", """
            CREATE TABLE attachment_receives (
                sender_peer_id TEXT NOT NULL,
                attachment_id TEXT NOT NULL,
                file_name BLOB NOT NULL,
                content_type BLOB NOT NULL,
                size_bytes INTEGER NOT NULL,
                chunk_size INTEGER NOT NULL,
                sha256 BLOB NOT NULL,
                created_utc INTEGER NOT NULL,
                updated_utc INTEGER NOT NULL,
                PRIMARY KEY(sender_peer_id, attachment_id)
            );

            CREATE INDEX ix_attachment_receives_updated
                ON attachment_receives(updated_utc, sender_peer_id, attachment_id);

            CREATE TABLE attachment_receive_chunks (
                sender_peer_id TEXT NOT NULL,
                attachment_id TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                payload BLOB NOT NULL,
                payload_length INTEGER NOT NULL,
                received_utc INTEGER NOT NULL,
                PRIMARY KEY(sender_peer_id, attachment_id, chunk_index),
                FOREIGN KEY(sender_peer_id, attachment_id)
                    REFERENCES attachment_receives(sender_peer_id, attachment_id)
                    ON DELETE CASCADE
            );
            """),
        new(4, "bound-attachment-receive-reservations", """
            CREATE TRIGGER attachment_receives_quota_before_insert
            BEFORE INSERT ON attachment_receives
            WHEN
                (SELECT COUNT(*) FROM attachment_receives) >= 16
                OR (SELECT COUNT(*) FROM attachment_receives WHERE sender_peer_id = NEW.sender_peer_id) >= 4
                OR COALESCE((SELECT SUM(size_bytes) FROM attachment_receives), 0) + NEW.size_bytes > 536870912
                OR COALESCE((SELECT SUM(size_bytes) FROM attachment_receives WHERE sender_peer_id = NEW.sender_peer_id), 0) + NEW.size_bytes > 209715200
            BEGIN
                SELECT RAISE(ABORT, 'attachment_receive_quota');
            END;
            """),
        new(5, "durable-attachment-send-outbox", """
            CREATE TABLE attachment_sends (
                sender_peer_id TEXT NOT NULL,
                recipient_peer_id TEXT NOT NULL,
                attachment_id TEXT NOT NULL,
                file_name BLOB NOT NULL,
                content_type BLOB NOT NULL,
                size_bytes INTEGER NOT NULL,
                chunk_size INTEGER NOT NULL,
                sha256 BLOB NOT NULL,
                created_utc INTEGER NOT NULL,
                updated_utc INTEGER NOT NULL,
                next_attempt_utc INTEGER NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                manifest_acknowledged INTEGER NOT NULL DEFAULT 0,
                last_failure BLOB NULL,
                PRIMARY KEY(sender_peer_id, recipient_peer_id, attachment_id)
            );

            CREATE INDEX ix_attachment_sends_due
                ON attachment_sends(next_attempt_utc, sender_peer_id, recipient_peer_id, attachment_id);

            CREATE TABLE attachment_send_chunks (
                sender_peer_id TEXT NOT NULL,
                recipient_peer_id TEXT NOT NULL,
                attachment_id TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                payload BLOB NOT NULL,
                payload_length INTEGER NOT NULL,
                acknowledged INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(sender_peer_id, recipient_peer_id, attachment_id, chunk_index),
                FOREIGN KEY(sender_peer_id, recipient_peer_id, attachment_id)
                    REFERENCES attachment_sends(sender_peer_id, recipient_peer_id, attachment_id)
                    ON DELETE CASCADE
                    DEFERRABLE INITIALLY DEFERRED
            );

            CREATE TRIGGER attachment_sends_quota_before_insert
            BEFORE INSERT ON attachment_sends
            WHEN
                (SELECT COUNT(*) FROM attachment_sends) >= 16
                OR (SELECT COUNT(*) FROM attachment_sends WHERE recipient_peer_id = NEW.recipient_peer_id) >= 4
                OR COALESCE((SELECT SUM(size_bytes) FROM attachment_sends), 0) + NEW.size_bytes > 536870912
                OR COALESCE((SELECT SUM(size_bytes) FROM attachment_sends WHERE recipient_peer_id = NEW.recipient_peer_id), 0) + NEW.size_bytes > 209715200
            BEGIN
                SELECT RAISE(ABORT, 'attachment_send_quota');
            END;
            """)
    ];

    private readonly string _connectionString;

    public SqliteSchemaMigrationRunner(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_utc INTEGER NOT NULL
            );
            """, cancellationToken);

        var legacyVersion = await ReadLegacySchemaVersionAsync(connection, transaction, cancellationToken);
        var applied = await ReadAppliedVersionsAsync(connection, transaction, cancellationToken);

        if (legacyVersion is > CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Local database schema version {legacyVersion} is newer than this Dyract build supports ({CurrentVersion}).");
        }

        if (applied.Count > 0 && applied.Max() > CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Local database migration version {applied.Max()} is newer than this Dyract build supports ({CurrentVersion}).");
        }

        ValidateContiguousHistory(applied);

        foreach (var migration in Migrations)
        {
            if (applied.Contains(migration.Version))
            {
                continue;
            }

            if (migration.Version == 1)
            {
                if (legacyVersion != 1)
                {
                    throw new InvalidOperationException(
                        "The existing local database could not be identified as the Dyract v1 schema and will not be modified automatically.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(migration.Sql))
            {
                await ExecuteAsync(connection, transaction, migration.Sql, cancellationToken);
            }

            await RecordMigrationAsync(connection, transaction, migration, cancellationToken);
            applied.Add(migration.Version);
        }

        ValidateContiguousHistory(applied);
        if (applied.Count != CurrentVersion || applied.Max() != CurrentVersion)
        {
            throw new InvalidOperationException("Local database migration history is incomplete.");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<int?> ReadLegacySchemaVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var tableCommand = connection.CreateCommand();
        tableCommand.Transaction = transaction;
        tableCommand.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name = 'schema_info';
            """;
        var tableCount = Convert.ToInt32(await tableCommand.ExecuteScalarAsync(cancellationToken));
        if (tableCount == 0)
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM schema_info ORDER BY version;";

        var versions = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(reader.GetInt32(0));
        }

        if (versions.Count != 1)
        {
            throw new InvalidOperationException("Legacy local schema metadata is malformed.");
        }

        return versions[0];
    }

    private static async Task<HashSet<int>> ReadAppliedVersionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM schema_migrations ORDER BY version;";

        var versions = new HashSet<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!versions.Add(reader.GetInt32(0)))
            {
                throw new InvalidOperationException("Local database migration history contains duplicate versions.");
            }
        }

        return versions;
    }

    private static void ValidateContiguousHistory(IReadOnlySet<int> versions)
    {
        if (versions.Count == 0)
        {
            return;
        }

        var maximum = versions.Max();
        for (var version = 1; version <= maximum; version++)
        {
            if (!versions.Contains(version))
            {
                throw new InvalidOperationException(
                    $"Local database migration history has a gap before version {maximum}.");
            }
        }
    }

    private static async Task RecordMigrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MigrationDefinition migration,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO schema_migrations(version, name, applied_utc)
            VALUES($version, $name, $applied_utc);
            """;
        command.Parameters.AddWithValue("$version", migration.Version);
        command.Parameters.AddWithValue("$name", migration.Name);
        command.Parameters.AddWithValue("$applied_utc", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private sealed record MigrationDefinition(int Version, string Name, string? Sql);
}
