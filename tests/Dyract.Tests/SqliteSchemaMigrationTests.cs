using Dyract.Crypto.Identity;
using Dyract.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dyract.Tests;

public sealed class SqliteSchemaMigrationTests
{
    [Fact]
    public async Task NewMigratingStore_RecordsAllMigrationsExactlyOnce()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var store = new MigratingLocalStore(databasePath, new FixedKeyProvider(0x41));
            await store.InitializeAsync();
            await store.InitializeAsync();

            var migrations = await ReadMigrationsAsync(databasePath);
            Assert.Collection(
                migrations,
                migration =>
                {
                    Assert.Equal(1, migration.Version);
                    Assert.Equal("baseline-v1", migration.Name);
                },
                migration =>
                {
                    Assert.Equal(2, migration.Version);
                    Assert.Equal("track-issued-contact-capability", migration.Name);
                },
                migration =>
                {
                    Assert.Equal(3, migration.Version);
                    Assert.Equal("durable-attachment-receive-state", migration.Name);
                },
                migration =>
                {
                    Assert.Equal(4, migration.Version);
                    Assert.Equal("bound-attachment-receive-reservations", migration.Name);
                },
                migration =>
                {
                    Assert.Equal(5, migration.Version);
                    Assert.Equal("durable-attachment-send-outbox", migration.Name);
                });

            Assert.True(await ColumnExistsAsync(databasePath, "contacts", "granted_capability"));
            Assert.True(await TableExistsAsync(databasePath, "attachment_receives"));
            Assert.True(await TableExistsAsync(databasePath, "attachment_receive_chunks"));
            Assert.True(await TriggerExistsAsync(databasePath, "attachment_receives_quota_before_insert"));
            Assert.True(await TableExistsAsync(databasePath, "attachment_sends"));
            Assert.True(await TableExistsAsync(databasePath, "attachment_send_chunks"));
            Assert.True(await TriggerExistsAsync(databasePath, "attachment_sends_quota_before_insert"));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task ExistingV1Database_IsUpgradedToCurrentVersionWithoutLosingEncryptedData()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x42);
            using var contactIdentity = PeerIdentity.Generate();

            var legacyStore = new SqliteLocalStore(databasePath, keyProvider);
            await legacyStore.InitializeAsync();
            await legacyStore.UpsertContactAsync(new ContactDraft(
                contactIdentity.PeerId.Value,
                contactIdentity.ExportPublicKey(),
                "Existing encrypted contact"));

            Assert.False(await ColumnExistsAsync(databasePath, "contacts", "granted_capability"));
            Assert.False(await TableExistsAsync(databasePath, "attachment_receives"));

            var migratingStore = new MigratingLocalStore(databasePath, keyProvider);
            await migratingStore.InitializeAsync();

            var contact = await migratingStore.GetContactAsync(contactIdentity.PeerId.Value);
            Assert.NotNull(contact);
            Assert.Equal("Existing encrypted contact", contact.DisplayName);
            Assert.True(await ColumnExistsAsync(databasePath, "contacts", "granted_capability"));
            Assert.True(await TableExistsAsync(databasePath, "attachment_receives"));
            Assert.True(await TableExistsAsync(databasePath, "attachment_receive_chunks"));
            Assert.True(await TriggerExistsAsync(databasePath, "attachment_receives_quota_before_insert"));
            Assert.True(await TableExistsAsync(databasePath, "attachment_sends"));
            Assert.True(await TableExistsAsync(databasePath, "attachment_send_chunks"));
            Assert.True(await TriggerExistsAsync(databasePath, "attachment_sends_quota_before_insert"));

            var migrations = await ReadMigrationsAsync(databasePath);
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, migrations.Select(value => value.Version).ToArray());
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task MigrationRunner_RejectsDatabaseFromNewerDyractBuild()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE schema_info(version INTEGER NOT NULL);
                    INSERT INTO schema_info(version) VALUES(99);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var runner = new SqliteSchemaMigrationRunner(databasePath);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => runner.ApplyAsync());

            Assert.Contains("newer than this Dyract build supports", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task MigrationRunner_RejectsMalformedLegacyMetadata()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE schema_info(version INTEGER NOT NULL);
                    INSERT INTO schema_info(version) VALUES(1);
                    INSERT INTO schema_info(version) VALUES(1);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var runner = new SqliteSchemaMigrationRunner(databasePath);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => runner.ApplyAsync());

            Assert.Contains("metadata is malformed", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static async Task<IReadOnlyList<(int Version, string Name)>> ReadMigrationsAsync(string databasePath)
    {
        var result = new List<(int Version, string Name)>();
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, name FROM schema_migrations ORDER BY version;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add((reader.GetInt32(0), reader.GetString(1)));
        }

        return result;
    }

    private static async Task<bool> TableExistsAsync(string databasePath, string table)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<bool> TriggerExistsAsync(string databasePath, string trigger)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name = $name;";
        command.Parameters.AddWithValue("$name", trigger);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<bool> ColumnExistsAsync(
        string databasePath,
        string table,
        string column)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dyract-migration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "local.db3");
    }

    private static void DeleteDatabaseFiles(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FixedKeyProvider(byte fill) : ILocalEncryptionKeyProvider
    {
        public ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Enumerable.Repeat(fill, 32).Select(value => (byte)value).ToArray());
    }
}
