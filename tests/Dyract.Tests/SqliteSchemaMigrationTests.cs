using Dyract.Crypto.Identity;
using Dyract.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dyract.Tests;

public sealed class SqliteSchemaMigrationTests
{
    [Fact]
    public async Task NewMigratingStore_RecordsBaselineMigrationExactlyOnce()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var store = new MigratingLocalStore(databasePath, new FixedKeyProvider(0x41));
            await store.InitializeAsync();
            await store.InitializeAsync();

            var migrations = await ReadMigrationsAsync(databasePath);
            var migration = Assert.Single(migrations);
            Assert.Equal(1, migration.Version);
            Assert.Equal("baseline-v1", migration.Name);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task ExistingV1Database_IsAdoptedWithoutLosingEncryptedData()
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

            var migratingStore = new MigratingLocalStore(databasePath, keyProvider);
            await migratingStore.InitializeAsync();

            var contact = await migratingStore.GetContactAsync(contactIdentity.PeerId.Value);
            Assert.NotNull(contact);
            Assert.Equal("Existing encrypted contact", contact.DisplayName);

            var migration = Assert.Single(await ReadMigrationsAsync(databasePath));
            Assert.Equal(1, migration.Version);
            Assert.Equal("baseline-v1", migration.Name);
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
