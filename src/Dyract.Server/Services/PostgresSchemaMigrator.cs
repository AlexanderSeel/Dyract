using Npgsql;

namespace Dyract.Server.Services;

public sealed class PostgresSchemaMigrator
{
    public const int CurrentVersion = 2;

    private const long AdvisoryLockKey = 0x445952414354; // ASCII "DYRACT"

    private static readonly MigrationDefinition[] Migrations =
    [
        new(1, "create-peer-identity", """
            CREATE TABLE IF NOT EXISTS peer_identity
            (
                peer_id       text PRIMARY KEY,
                public_key    bytea NOT NULL,
                registered_at timestamptz NOT NULL
            );
            """),
        new(2, "persist-capability-revocations", """
            CREATE TABLE IF NOT EXISTS capability_revocation
            (
                issuer_peer_id text NOT NULL REFERENCES peer_identity(peer_id) ON DELETE CASCADE,
                capability_id  text NOT NULL,
                expires_at     timestamptz NOT NULL,
                PRIMARY KEY (issuer_peer_id, capability_id)
            );

            CREATE INDEX IF NOT EXISTS ix_capability_revocation_expires_at
                ON capability_revocation(expires_at);
            """)
    ];

    private readonly NpgsqlDataSource _dataSource;

    public PostgresSchemaMigrator(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(@lock_key);",
            cancellationToken,
            new NpgsqlParameter("lock_key", AdvisoryLockKey));

        await ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS dyract_schema_migrations
            (
                version     integer PRIMARY KEY,
                name        text NOT NULL,
                applied_at  timestamptz NOT NULL
            );
            """, cancellationToken);

        var appliedVersions = await ReadAppliedVersionsAsync(connection, transaction, cancellationToken);
        if (appliedVersions.Count > 0 && appliedVersions.Max() > CurrentVersion)
        {
            throw new InvalidOperationException(
                $"PostgreSQL schema migration version {appliedVersions.Max()} is newer than this Dyract build supports ({CurrentVersion}).");
        }

        ValidateContiguousHistory(appliedVersions);

        foreach (var migration in Migrations)
        {
            if (appliedVersions.Contains(migration.Version))
            {
                continue;
            }

            await ExecuteAsync(connection, transaction, migration.Sql, cancellationToken);

            switch (migration.Version)
            {
                case 1:
                    await ValidatePeerIdentitySchemaAsync(connection, transaction, cancellationToken);
                    break;
                case 2:
                    await ValidateCapabilityRevocationSchemaAsync(connection, transaction, cancellationToken);
                    break;
            }

            await RecordMigrationAsync(connection, transaction, migration, cancellationToken);
            appliedVersions.Add(migration.Version);
        }

        ValidateContiguousHistory(appliedVersions);
        if (appliedVersions.Count != CurrentVersion || appliedVersions.Max() != CurrentVersion)
        {
            throw new InvalidOperationException("PostgreSQL schema migration history is incomplete.");
        }

        // Validate the final physical schema on every startup, not only when a migration
        // is first recorded. This catches manual drift/corruption behind an otherwise
        // apparently complete migration ledger and fails closed before serving requests.
        await ValidatePeerIdentitySchemaAsync(connection, transaction, cancellationToken);
        await ValidateCapabilityRevocationSchemaAsync(connection, transaction, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<HashSet<int>> ReadAppliedVersionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT version FROM dyract_schema_migrations ORDER BY version;",
            connection,
            transaction);

        var versions = new HashSet<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var version = reader.GetInt32(0);
            if (!versions.Add(version))
            {
                throw new InvalidOperationException("PostgreSQL schema migration history contains duplicate versions.");
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
                    $"PostgreSQL schema migration history has a gap before version {maximum}.");
            }
        }
    }

    private static async Task ValidatePeerIdentitySchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = 'peer_identity'
                      AND column_name = 'peer_id'
                      AND data_type = 'text'
                      AND is_nullable = 'NO'
                ),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = 'peer_identity'
                      AND column_name = 'public_key'
                      AND data_type = 'bytea'
                      AND is_nullable = 'NO'
                ),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = 'peer_identity'
                      AND column_name = 'registered_at'
                      AND data_type = 'timestamp with time zone'
                      AND is_nullable = 'NO'
                ),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint c
                    JOIN pg_class t ON t.oid = c.conrelid
                    JOIN pg_namespace n ON n.oid = t.relnamespace
                    WHERE c.contype = 'p'
                      AND n.nspname = current_schema()
                      AND t.relname = 'peer_identity'
                      AND pg_get_constraintdef(c.oid) = 'PRIMARY KEY (peer_id)'
                );
            """,
            connection,
            transaction);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            !reader.GetBoolean(0) ||
            !reader.GetBoolean(1) ||
            !reader.GetBoolean(2) ||
            !reader.GetBoolean(3))
        {
            throw new InvalidOperationException(
                "Existing PostgreSQL peer_identity schema does not match the Dyract v1 identity schema and will not be modified automatically.");
        }
    }

    private static async Task ValidateCapabilityRevocationSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = 'capability_revocation'
                      AND column_name = 'issuer_peer_id'
                      AND data_type = 'text'
                      AND is_nullable = 'NO'
                ),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = 'capability_revocation'
                      AND column_name = 'capability_id'
                      AND data_type = 'text'
                      AND is_nullable = 'NO'
                ),
                EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = 'capability_revocation'
                      AND column_name = 'expires_at'
                      AND data_type = 'timestamp with time zone'
                      AND is_nullable = 'NO'
                ),
                EXISTS (
                    SELECT 1
                    FROM pg_constraint c
                    JOIN pg_class t ON t.oid = c.conrelid
                    JOIN pg_namespace n ON n.oid = t.relnamespace
                    WHERE c.contype = 'p'
                      AND n.nspname = current_schema()
                      AND t.relname = 'capability_revocation'
                      AND pg_get_constraintdef(c.oid) = 'PRIMARY KEY (issuer_peer_id, capability_id)'
                ),
                NOT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = 'capability_revocation'
                      AND column_name ILIKE '%grantee%'
                );
            """,
            connection,
            transaction);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            !reader.GetBoolean(0) ||
            !reader.GetBoolean(1) ||
            !reader.GetBoolean(2) ||
            !reader.GetBoolean(3) ||
            !reader.GetBoolean(4))
        {
            throw new InvalidOperationException(
                "Existing PostgreSQL capability_revocation schema does not match Dyract's metadata-minimized revocation schema and will not be modified automatically.");
        }
    }

    private static async Task RecordMigrationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MigrationDefinition migration,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO dyract_schema_migrations(version, name, applied_at)
            VALUES (@version, @name, now());
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("version", migration.Version);
        command.Parameters.AddWithValue("name", migration.Name);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        if (parameters.Length > 0)
        {
            command.Parameters.AddRange(parameters);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record MigrationDefinition(int Version, string Name, string Sql);
}

public sealed class PostgresSchemaInitializer : IHostedService
{
    private readonly PostgresSchemaMigrator _migrator;
    private readonly ILogger<PostgresSchemaInitializer> _logger;

    public PostgresSchemaInitializer(
        NpgsqlDataSource dataSource,
        ILogger<PostgresSchemaInitializer> logger)
    {
        _migrator = new PostgresSchemaMigrator(dataSource);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _migrator.ApplyAsync(cancellationToken);
        _logger.LogInformation(
            "PostgreSQL schema migrations are current at version {Version}.",
            PostgresSchemaMigrator.CurrentVersion);
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
