namespace Dyract.Storage;

/// <summary>
/// Stores the reachability capability this installation issued to a saved contact.
/// This is separate from <see cref="LocalContact.Capability"/>, which represents
/// permission the remote contact issued to the local peer.
/// </summary>
public interface IIssuedCapabilityStore
{
    Task<string?> GetIssuedCapabilityAsync(
        string peerId,
        CancellationToken cancellationToken = default);

    Task SaveIssuedCapabilityAsync(
        string peerId,
        string capability,
        CancellationToken cancellationToken = default);

    Task ClearIssuedCapabilityAsync(
        string peerId,
        CancellationToken cancellationToken = default);
}
