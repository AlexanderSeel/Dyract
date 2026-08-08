using System.Collections.Concurrent;
using Dyract.Core.Identity;

namespace Dyract.Server.Services;

/// <summary>
/// Ephemeral pre-expiry capability revocations. The store deliberately keeps no grantee/contact
/// relationship: only issuer PeerId + opaque capability ID + natural expiry are retained.
/// </summary>
public sealed class CapabilityRevocationStore
{
    public const int MaximumActiveRevocationsPerIssuer = 512;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTimeOffset>> _byIssuer =
        new(StringComparer.Ordinal);

    public CapabilityRevocationResult Revoke(
        PeerId issuer,
        string capabilityId,
        DateTimeOffset expiresAt,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        if (expiresAt <= now)
        {
            return CapabilityRevocationResult.Expired;
        }

        var issuerRevocations = _byIssuer.GetOrAdd(
            issuer.Value,
            static _ => new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal));

        RemoveExpired(issuerRevocations, now);

        if (issuerRevocations.TryGetValue(capabilityId, out var existing))
        {
            if (existing < expiresAt)
            {
                issuerRevocations[capabilityId] = expiresAt;
            }

            return CapabilityRevocationResult.AlreadyRevoked;
        }

        if (issuerRevocations.Count >= MaximumActiveRevocationsPerIssuer)
        {
            return CapabilityRevocationResult.CapacityExceeded;
        }

        return issuerRevocations.TryAdd(capabilityId, expiresAt)
            ? CapabilityRevocationResult.Revoked
            : CapabilityRevocationResult.AlreadyRevoked;
    }

    public bool IsRevoked(PeerId issuer, string capabilityId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);

        if (!_byIssuer.TryGetValue(issuer.Value, out var issuerRevocations))
        {
            return false;
        }

        RemoveExpired(issuerRevocations, now);
        return issuerRevocations.TryGetValue(capabilityId, out var expiresAt) && expiresAt > now;
    }

    public int CountActive(PeerId issuer, DateTimeOffset now)
    {
        if (!_byIssuer.TryGetValue(issuer.Value, out var issuerRevocations))
        {
            return 0;
        }

        RemoveExpired(issuerRevocations, now);
        return issuerRevocations.Count;
    }

    private static void RemoveExpired(
        ConcurrentDictionary<string, DateTimeOffset> issuerRevocations,
        DateTimeOffset now)
    {
        foreach (var entry in issuerRevocations)
        {
            if (entry.Value <= now)
            {
                issuerRevocations.TryRemove(entry.Key, out _);
            }
        }
    }
}

public enum CapabilityRevocationResult
{
    Revoked = 0,
    AlreadyRevoked = 1,
    Expired = 2,
    CapacityExceeded = 3
}
