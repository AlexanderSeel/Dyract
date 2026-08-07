using System.Collections.Concurrent;
using System.Security.Cryptography;
using Dyract.Core.Identity;

namespace Dyract.Server.Services;

public interface IIdentityStore
{
    ValueTask<IdentityRegistrationResult> RegisterAsync(
        PeerId peerId,
        byte[] publicKey,
        DateTimeOffset registeredAt,
        CancellationToken cancellationToken = default);

    ValueTask<RegisteredPeer?> GetAsync(
        PeerId peerId,
        CancellationToken cancellationToken = default);
}

public enum IdentityRegistrationStatus
{
    Created,
    Existing,
    Conflict
}

public sealed record IdentityRegistrationResult(
    IdentityRegistrationStatus Status,
    RegisteredPeer Peer)
{
    public bool IsAccepted => Status is IdentityRegistrationStatus.Created or IdentityRegistrationStatus.Existing;
}

public sealed class InMemoryIdentityStore : IIdentityStore
{
    private readonly ConcurrentDictionary<string, RegisteredPeer> _peers = new(StringComparer.Ordinal);

    public ValueTask<IdentityRegistrationResult> RegisterAsync(
        PeerId peerId,
        byte[] publicKey,
        DateTimeOffset registeredAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(publicKey);

        var candidate = new RegisteredPeer(peerId, publicKey.ToArray(), registeredAt);

        if (_peers.TryAdd(peerId.Value, candidate))
        {
            return ValueTask.FromResult(new IdentityRegistrationResult(
                IdentityRegistrationStatus.Created,
                candidate));
        }

        var existing = _peers[peerId.Value];
        var status = CryptographicOperations.FixedTimeEquals(existing.PublicKey, publicKey)
            ? IdentityRegistrationStatus.Existing
            : IdentityRegistrationStatus.Conflict;

        return ValueTask.FromResult(new IdentityRegistrationResult(status, existing));
    }

    public ValueTask<RegisteredPeer?> GetAsync(
        PeerId peerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _peers.TryGetValue(peerId.Value, out var peer);
        return ValueTask.FromResult(peer);
    }
}

public sealed record RegisteredPeer(
    PeerId PeerId,
    byte[] PublicKey,
    DateTimeOffset RegisteredAt);
