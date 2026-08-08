using Dyract.Crypto.Identity;
using Dyract.Server.Services;
using Npgsql;
using Xunit;

namespace Dyract.Tests;

public sealed class PostgresSchemaMigrationTests
{
    private const string ConnectionEnvironmentVariable = "DYRACT_POSTGRES_TEST_CONNECTION";

    [Fact]
    public async Task FreshSchema_MigratesIdempotentlyAndStoresWork()
    {
        var baseConnectionString = GetConnectionStringOrSkip();
        if (baseConnectionString is null)
        {
            return;
        }

        await using var scope = await PostgresTestSchema.CreateAsync(baseConnectionString);
        await using var dataSource = NpgsqlDataSource.Create(scope.ConnectionString);
        var migrator = new PostgresSchemaMigrator(dataSource);

        await migrator.ApplyAsync();
        await migrator.ApplyAsync();

        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
                         "SELECT version, name FROM dyract_schema_migrations ORDER BY version;",
                         connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal("create-peer-identity", reader.GetString(1));
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2, reader.GetInt32(0));
            Assert.Equal("persist-capability-revocations", reader.GetString(1));
            Assert.False(await reader.ReadAsync());
        }

        using var identity = PeerIdentity.Generate();
        var identityStore = new PostgresIdentityStore(dataSource);
        var registeredAt = DateTimeOffset.UtcNow;
        var registration = await identityStore.RegisterAsync(
            identity.PeerId,
            identity.ExportPublicKey(),
            registeredAt);

        Assert.Equal(IdentityRegistrationStatus.Created, registration.Status);
        var fetched = await identityStore.GetAsync(identity.PeerId);
        Assert.NotNull(fetched);
        Assert.Equal(identity.PeerId, fetched.PeerId);
        Assert.Equal(identity.ExportPublicKey(), fetched.PublicKey);

        var now = DateTimeOffset.UtcNow;
        var capabilityId = new string('a', 32);
        var firstStoreInstance = new PostgresCapabilityRevocationStore(dataSource);
        Assert.Equal(
            CapabilityRevocationResult.Revoked,
            await firstStoreInstance.RevokeAsync(
                identity.PeerId,
                capabilityId,
                now.AddMinutes(10),
                now));

        // A new store instance simulates a fresh server process using the same PostgreSQL state.
        var restartedStoreInstance = new PostgresCapabilityRevocationStore(dataSource);
        Assert.True(await restartedStoreInstance.IsRevokedAsync(identity.PeerId, capabilityId, now.AddMinutes(1)));
        Assert.False(await restartedStoreInstance.IsRevokedAsync(identity.PeerId, capabilityId, now.AddMinutes(11)));

        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
                         """
                         SELECT count(*)
                         FROM information_schema.columns
                         WHERE table_schema = current_schema()
                           AND table_name = 'capability_revocation'
                           AND column_name ILIKE '%grantee%';
                         """,
                         connection))
        {
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task ConcurrentMigrators_AreSerializedByAdvisoryLock()
    {
        var baseConnectionString = GetConnectionStringOrSkip();
        if (baseConnectionString is null)
        {
            return;
        }

        await using var scope = await PostgresTestSchema.CreateAsync(baseConnectionString);
        await using var dataSource = NpgsqlDataSource.Create(scope.ConnectionString);

        await Task.WhenAll(
            Enumerable.Range(0, 4)
                .Select(_ => new PostgresSchemaMigrator(dataSource).ApplyAsync()));

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM dyract_schema_migrations;",
            connection);
        Assert.Equal((long)PostgresSchemaMigrator.CurrentVersion, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ExistingMalformedIdentityTable_IsRejectedWithoutRecordingMigration()
    {
        var baseConnectionString = GetConnectionStringOrSkip();
        if (baseConnectionString is null)
        {
            return;
        }

        await using var scope = await PostgresTestSchema.CreateAsync(baseConnectionString);
        await using var dataSource = NpgsqlDataSource.Create(scope.ConnectionString);

        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
                         """
                         CREATE TABLE peer_identity
                         (
                             peer_id text NOT NULL,
                             public_key bytea NOT NULL,
                             registered_at timestamptz NOT NULL
                         );
                         """,
                         connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        var migrator = new PostgresSchemaMigrator(dataSource);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => migrator.ApplyAsync());
        Assert.Contains("does not match", exception.Message, StringComparison.OrdinalIgnoreCase);

        await using var verifyConnection = await dataSource.OpenConnectionAsync();
        await using var verifyCommand = new NpgsqlCommand(
            "SELECT to_regclass('dyract_schema_migrations') IS NULL;",
            verifyConnection);
        Assert.True((bool)(await verifyCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ExistingMalformedRevocationTable_IsRejectedWithoutRecordingVersionTwo()
    {
        var baseConnectionString = GetConnectionStringOrSkip();
        if (baseConnectionString is null)
        {
            return;
        }

        await using var scope = await PostgresTestSchema.CreateAsync(baseConnectionString);
        await using var dataSource = NpgsqlDataSource.Create(scope.ConnectionString);

        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
                         """
                         CREATE TABLE peer_identity
                         (
                             peer_id text PRIMARY KEY,
                             public_key bytea NOT NULL,
                             registered_at timestamptz NOT NULL
                         );
                         CREATE TABLE dyract_schema_migrations
                         (
                             version integer PRIMARY KEY,
                             name text NOT NULL,
                             applied_at timestamptz NOT NULL
                         );
                         INSERT INTO dyract_schema_migrations(version, name, applied_at)
                         VALUES (1, 'create-peer-identity', now());
                         CREATE TABLE capability_revocation
                         (
                             issuer_peer_id text NOT NULL,
                             capability_id text NOT NULL,
                             grantee_peer_id text NOT NULL,
                             expires_at timestamptz NOT NULL,
                             PRIMARY KEY (issuer_peer_id, capability_id)
                         );
                         """,
                         connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new PostgresSchemaMigrator(dataSource).ApplyAsync());
        Assert.Contains("metadata-minimized", exception.Message, StringComparison.OrdinalIgnoreCase);

        await using var verifyConnection = await dataSource.OpenConnectionAsync();
        await using var verifyCommand = new NpgsqlCommand(
            "SELECT count(*) FROM dyract_schema_migrations WHERE version = 2;",
            verifyConnection);
        Assert.Equal(0L, (long)(await verifyCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task FutureMigrationVersion_IsRejected()
    {
        var baseConnectionString = GetConnectionStringOrSkip();
        if (baseConnectionString is null)
        {
            return;
        }

        await using var scope = await PostgresTestSchema.CreateAsync(baseConnectionString);
        await using var dataSource = NpgsqlDataSource.Create(scope.ConnectionString);

        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
                         """
                         CREATE TABLE dyract_schema_migrations
                         (
                             version integer PRIMARY KEY,
                             name text NOT NULL,
                             applied_at timestamptz NOT NULL
                         );
                         INSERT INTO dyract_schema_migrations(version, name, applied_at)
                         VALUES (99, 'future', now());
                         """,
                         connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new PostgresSchemaMigrator(dataSource).ApplyAsync());
        Assert.Contains("newer than this Dyract build", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetConnectionStringOrSkip()
        => Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);

    private sealed class PostgresTestSchema : IAsyncDisposable
    {
        private readonly string _adminConnectionString;

        private PostgresTestSchema(
            string adminConnectionString,
            string schema,
            string connectionString)
        {
            _adminConnectionString = adminConnectionString;
            Schema = schema;
            ConnectionString = connectionString;
        }

        public string Schema { get; }
        public string ConnectionString { get; }

        public static async Task<PostgresTestSchema> CreateAsync(string adminConnectionString)
        {
            var schema = $"dyract_test_{Guid.NewGuid():N}";
            await using var connection = new NpgsqlConnection(adminConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\";", connection);
            await command.ExecuteNonQueryAsync();

            var builder = new NpgsqlConnectionStringBuilder(adminConnectionString)
            {
                SearchPath = schema
            };
            return new PostgresTestSchema(adminConnectionString, schema, builder.ConnectionString);
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE;", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
