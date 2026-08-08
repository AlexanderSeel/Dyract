using Dyract.Core.Identity;
using Npgsql;

namespace Dyract.Server.Services;

/// <summary>
/// Durable capability revocations for PostgreSQL-backed directory deployments.
/// Stores only issuer PeerId + opaque capability ID + natural expiry; no grantee/contact graph.
/// </summary>
public sealed class PostgresCapabilityRevocationStore : ICapabilityRevocationStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresCapabilityRevocationStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async ValueTask<CapabilityRevocationResult> RevokeAsync(
        PeerId issuer,
        string capabilityId,
        DateTimeOffset expiresAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        if (expiresAt <= now)
        {
            return CapabilityRevocationResult.Expired;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Serialize capacity checks for one issuer without creating a separate lock table.
        await using (var lockCommand = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(hashtextextended(@issuer_peer_id, 0));",
                         connection,
                         transaction))
        {
            lockCommand.Parameters.AddWithValue("issuer_peer_id", issuer.Value);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cleanup = new NpgsqlCommand(
                         """
                         DELETE FROM capability_revocation
                         WHERE issuer_peer_id = @issuer_peer_id
                           AND expires_at <= @now;
                         """,
                         connection,
                         transaction))
        {
            cleanup.Parameters.AddWithValue("issuer_peer_id", issuer.Value);
            cleanup.Parameters.AddWithValue("now", now.UtcDateTime);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        DateTimeOffset? existingExpiry = null;
        await using (var existing = new NpgsqlCommand(
                         """
                         SELECT expires_at
                         FROM capability_revocation
                         WHERE issuer_peer_id = @issuer_peer_id
                           AND capability_id = @capability_id;
                         """,
                         connection,
                         transaction))
        {
            existing.Parameters.AddWithValue("issuer_peer_id", issuer.Value);
            existing.Parameters.AddWithValue("capability_id", capabilityId);
            var value = await existing.ExecuteScalarAsync(cancellationToken);
            if (value is DateTime dateTime)
            {
                existingExpiry = new DateTimeOffset(
                    dateTime.Kind == DateTimeKind.Utc
                        ? dateTime
                        : DateTime.SpecifyKind(dateTime, DateTimeKind.Utc));
            }
        }

        if (existingExpiry is not null)
        {
            if (existingExpiry < expiresAt)
            {
                await using var update = new NpgsqlCommand(
                    """
                    UPDATE capability_revocation
                    SET expires_at = @expires_at
                    WHERE issuer_peer_id = @issuer_peer_id
                      AND capability_id = @capability_id;
                    """,
                    connection,
                    transaction);
                update.Parameters.AddWithValue("expires_at", expiresAt.UtcDateTime);
                update.Parameters.AddWithValue("issuer_peer_id", issuer.Value);
                update.Parameters.AddWithValue("capability_id", capabilityId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return CapabilityRevocationResult.AlreadyRevoked;
        }

        long activeCount;
        await using (var count = new NpgsqlCommand(
                         """
                         SELECT count(*)
                         FROM capability_revocation
                         WHERE issuer_peer_id = @issuer_peer_id
                           AND expires_at > @now;
                         """,
                         connection,
                         transaction))
        {
            count.Parameters.AddWithValue("issuer_peer_id", issuer.Value);
            count.Parameters.AddWithValue("now", now.UtcDateTime);
            activeCount = (long)(await count.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("PostgreSQL revocation count returned no value."));
        }

        if (activeCount >= CapabilityRevocationStore.MaximumActiveRevocationsPerIssuer)
        {
            await transaction.RollbackAsync(cancellationToken);
            return CapabilityRevocationResult.CapacityExceeded;
        }

        await using (var insert = new NpgsqlCommand(
                         """
                         INSERT INTO capability_revocation(issuer_peer_id, capability_id, expires_at)
                         VALUES (@issuer_peer_id, @capability_id, @expires_at);
                         """,
                         connection,
                         transaction))
        {
            insert.Parameters.AddWithValue("issuer_peer_id", issuer.Value);
            insert.Parameters.AddWithValue("capability_id", capabilityId);
            insert.Parameters.AddWithValue("expires_at", expiresAt.UtcDateTime);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return CapabilityRevocationResult.Revoked;
    }

    public async ValueTask<bool> IsRevokedAsync(
        PeerId issuer,
        string capabilityId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM capability_revocation
                WHERE issuer_peer_id = @issuer_peer_id
                  AND capability_id = @capability_id
                  AND expires_at > @now
            );
            """,
            connection);
        command.Parameters.AddWithValue("issuer_peer_id", issuer.Value);
        command.Parameters.AddWithValue("capability_id", capabilityId);
        command.Parameters.AddWithValue("now", now.UtcDateTime);

        return (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL revocation lookup returned no value."));
    }
}
