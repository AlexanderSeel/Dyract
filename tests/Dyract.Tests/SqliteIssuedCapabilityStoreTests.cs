using Dyract.Crypto.Identity;
using Dyract.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dyract.Tests;

public sealed class SqliteIssuedCapabilityStoreTests
{
    [Fact]
    public async Task SaveReadClear_RoundTripsCapabilityWithoutPlaintextAtRest()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x51);
            var localStore = new MigratingLocalStore(databasePath, keyProvider);
            using var contactIdentity = PeerIdentity.Generate();
            await localStore.UpsertContactAsync(new ContactDraft(
                contactIdentity.PeerId.Value,
                contactIdentity.ExportPublicKey(),
                "Remote"));

            var store = new SqliteIssuedCapabilityStore(databasePath, keyProvider, localStore);
            const string capability = "dyract://pair/v1/test-sensitive-issued-capability";

            await store.SaveIssuedCapabilityAsync(contactIdentity.PeerId.Value, capability);
            Assert.Equal(capability, await store.GetIssuedCapabilityAsync(contactIdentity.PeerId.Value));

            var raw = await ReadRawIssuedCapabilityAsync(databasePath, contactIdentity.PeerId.Value);
            Assert.NotNull(raw);
            Assert.DoesNotContain(
                System.Text.Encoding.UTF8.GetBytes("test-sensitive-issued-capability"),
                raw!);

            await store.ClearIssuedCapabilityAsync(contactIdentity.PeerId.Value);
            Assert.Null(await store.GetIssuedCapabilityAsync(contactIdentity.PeerId.Value));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task WrongEncryptionKey_CannotReadIssuedCapability()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x52);
            var localStore = new MigratingLocalStore(databasePath, keyProvider);
            using var contactIdentity = PeerIdentity.Generate();
            await localStore.UpsertContactAsync(new ContactDraft(
                contactIdentity.PeerId.Value,
                contactIdentity.ExportPublicKey(),
                "Remote"));

            var writer = new SqliteIssuedCapabilityStore(databasePath, keyProvider, localStore);
            await writer.SaveIssuedCapabilityAsync(contactIdentity.PeerId.Value, "dyract://pair/v1/encrypted");

            var wrongStore = new MigratingLocalStore(databasePath, new FixedKeyProvider(0x53));
            var reader = new SqliteIssuedCapabilityStore(
                databasePath,
                new FixedKeyProvider(0x53),
                wrongStore);

            await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(
                () => reader.GetIssuedCapabilityAsync(contactIdentity.PeerId.Value));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task UnknownContact_CannotReceiveIssuedCapability()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var keyProvider = new FixedKeyProvider(0x54);
            var localStore = new MigratingLocalStore(databasePath, keyProvider);
            await localStore.InitializeAsync();
            using var peer = PeerIdentity.Generate();
            var store = new SqliteIssuedCapabilityStore(databasePath, keyProvider, localStore);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.SaveIssuedCapabilityAsync(peer.PeerId.Value, "dyract://pair/v1/value"));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static async Task<byte[]?> ReadRawIssuedCapabilityAsync(string databasePath, string peerId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT granted_capability FROM contacts WHERE peer_id = $peer_id;";
        command.Parameters.AddWithValue("$peer_id", peerId);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull or null ? null : (byte[])value;
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dyract-issued-capability-tests", Guid.NewGuid().ToString("N"));
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
