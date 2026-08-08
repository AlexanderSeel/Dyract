using System.Security.Cryptography;
using System.Text;
using Dyract.Core.Identity;
using Microsoft.Data.Sqlite;

namespace Dyract.Storage;

public sealed class SqliteIssuedCapabilityStore : IIssuedCapabilityStore
{
    private const int EncryptionNonceSize = 12;
    private const int EncryptionTagSize = 16;
    private const byte EncryptionFormatVersion = 1;
    private const int MaximumCapabilityLength = 32_768;

    private readonly string _connectionString;
    private readonly ILocalEncryptionKeyProvider _keyProvider;
    private readonly ILocalStore _localStore;

    public SqliteIssuedCapabilityStore(
        string databasePath,
        ILocalEncryptionKeyProvider keyProvider,
        ILocalStore localStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _localStore = localStore ?? throw new ArgumentNullException(nameof(localStore));

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public async Task<string?> GetIssuedCapabilityAsync(
        string peerId,
        CancellationToken cancellationToken = default)
    {
        var parsed = ParsePeerId(peerId);
        await _localStore.InitializeAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT granted_capability
            FROM contacts
            WHERE peer_id = $peer_id;
            """;
        command.Parameters.AddWithValue("$peer_id", parsed.Value);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null)
        {
            throw new InvalidOperationException("Issued capability can only be read for a saved contact.");
        }

        if (value is DBNull)
        {
            return null;
        }

        return await UnprotectTextAsync(
            (byte[])value,
            Context(parsed.Value),
            cancellationToken);
    }

    public async Task SaveIssuedCapabilityAsync(
        string peerId,
        string capability,
        CancellationToken cancellationToken = default)
    {
        var parsed = ParsePeerId(peerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        if (capability.Length > MaximumCapabilityLength)
        {
            throw new ArgumentException("Issued capability is too large.", nameof(capability));
        }

        await _localStore.InitializeAsync(cancellationToken);
        var protectedValue = await ProtectTextAsync(capability, Context(parsed.Value), cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE contacts
            SET granted_capability = $capability,
                updated_utc = $updated_utc
            WHERE peer_id = $peer_id;
            """;
        command.Parameters.Add("$capability", SqliteType.Blob).Value = protectedValue;
        command.Parameters.AddWithValue("$updated_utc", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$peer_id", parsed.Value);

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Issued capability can only be stored for a saved contact.");
        }
    }

    public async Task ClearIssuedCapabilityAsync(
        string peerId,
        CancellationToken cancellationToken = default)
    {
        var parsed = ParsePeerId(peerId);
        await _localStore.InitializeAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE contacts
            SET granted_capability = NULL,
                updated_utc = $updated_utc
            WHERE peer_id = $peer_id;
            """;
        command.Parameters.AddWithValue("$updated_utc", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$peer_id", parsed.Value);

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Issued capability can only be cleared for a saved contact.");
        }
    }

    private static PeerId ParsePeerId(string peerId)
    {
        if (!PeerId.TryParse(peerId, out var parsed))
        {
            throw new ArgumentException("PeerId is invalid.", nameof(peerId));
        }

        return parsed;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async ValueTask<byte[]> ProtectTextAsync(
        string value,
        string context,
        CancellationToken cancellationToken)
    {
        var key = await GetEncryptionKeyAsync(cancellationToken);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var nonce = RandomNumberGenerator.GetBytes(EncryptionNonceSize);
        var tag = new byte[EncryptionTagSize];
        var ciphertext = new byte[plaintext.Length];
        var associatedData = Encoding.UTF8.GetBytes(context);

        try
        {
            using var aes = new AesGcm(key, EncryptionTagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

            var result = new byte[1 + nonce.Length + tag.Length + ciphertext.Length];
            result[0] = EncryptionFormatVersion;
            nonce.CopyTo(result, 1);
            tag.CopyTo(result, 1 + nonce.Length);
            ciphertext.CopyTo(result, 1 + nonce.Length + tag.Length);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async ValueTask<string> UnprotectTextAsync(
        byte[] protectedValue,
        string context,
        CancellationToken cancellationToken)
    {
        if (protectedValue.Length < 1 + EncryptionNonceSize + EncryptionTagSize ||
            protectedValue[0] != EncryptionFormatVersion)
        {
            throw new CryptographicException("Issued capability has an unsupported encrypted format.");
        }

        var key = await GetEncryptionKeyAsync(cancellationToken);
        var nonce = protectedValue.AsSpan(1, EncryptionNonceSize);
        var tag = protectedValue.AsSpan(1 + EncryptionNonceSize, EncryptionTagSize);
        var ciphertext = protectedValue.AsSpan(1 + EncryptionNonceSize + EncryptionTagSize);
        var plaintext = new byte[ciphertext.Length];
        var associatedData = Encoding.UTF8.GetBytes(context);

        try
        {
            using var aes = new AesGcm(key, EncryptionTagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async ValueTask<byte[]> GetEncryptionKeyAsync(CancellationToken cancellationToken)
    {
        var key = await _keyProvider.GetOrCreateKeyAsync(cancellationToken);
        if (key.Length != 32)
        {
            throw new CryptographicException("Dyract local storage requires a 256-bit encryption key.");
        }

        return key;
    }

    private static string Context(string peerId)
        => $"dyract:v2:contact:{peerId}:granted-capability";
}
