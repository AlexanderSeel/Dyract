using System.Security.Cryptography;
using Dyract.Core.Identity;
using Npgsql;

namespace Dyract.Server.Services;

public sealed class PostgresIdentityStore : IIdentityStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresIdentityStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async ValueTask<IdentityRegistrationResult> RegisterAsync(
        PeerId peerId,
        byte[] publicKey,
        DateTimeOffset registeredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publicKey);

        var candidate = new RegisteredPeer(
            peerId,
            publicKey.ToArray(),
            registeredAt.ToUniversalTime());

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO peer_identity (peer_id, public_key, registered_at)
            VALUES (@peer_id, @public_key, @registered_at)
            ON CONFLICT (peer_id) DO NOTHING;
            """,
            connection))
        {
            insert.Parameters.AddWithValue("peer_id", candidate.PeerId.Value);
            insert.Parameters.AddWithValue("public_key", candidate.PublicKey);
            insert.Parameters.AddWithValue("registered_at", candidate.RegisteredAt.UtcDateTime);

            var inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
            if (inserted == 1)
            {
                return new IdentityRegistrationResult(
                    IdentityRegistrationStatus.Created,
                    candidate);
            }
        }

        var existing = await GetAsync(connection, peerId, cancellationToken)
            ?? throw new InvalidOperationException("Peer identity disappeared after a registration conflict.");

        var status = CryptographicOperations.FixedTimeEquals(existing.PublicKey, publicKey)
            ? IdentityRegistrationStatus.Existing
            : IdentityRegistrationStatus.Conflict;

        return new IdentityRegistrationResult(status, existing);
    }

    public async ValueTask<RegisteredPeer?> GetAsync(
        PeerId peerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await GetAsync(connection, peerId, cancellationToken);
    }

    private static async Task<RegisteredPeer?> GetAsync(
        NpgsqlConnection connection,
        PeerId peerId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT peer_id, public_key, registered_at
            FROM peer_identity
            WHERE peer_id = @peer_id;
            """,
            connection);

        command.Parameters.AddWithValue("peer_id", peerId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var storedPeerId = new PeerId(reader.GetString(0));
        var publicKey = reader.GetFieldValue<byte[]>(1);
        var registeredAtUtc = reader.GetFieldValue<DateTime>(2);

        if (registeredAtUtc.Kind != DateTimeKind.Utc)
        {
            registeredAtUtc = DateTime.SpecifyKind(registeredAtUtc, DateTimeKind.Utc);
        }

        return new RegisteredPeer(
            storedPeerId,
            publicKey,
            new DateTimeOffset(registeredAtUtc));
    }
}
