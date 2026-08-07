using System.Net;
using System.Net.Sockets;
using Dyract.Core.Identity;
using Dyract.Protocol;

namespace Dyract.Transport;

public static class PeerConnectionDescriptorFactory
{
    public static PeerConnectionDescriptor Create(
        ResolvePeerResponse response,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!PeerId.TryParse(response.PeerId, out var peerId))
        {
            throw new ArgumentException("Resolved peer has an invalid PeerId.", nameof(response));
        }

        if (!response.IsReachable)
        {
            throw new InvalidOperationException("Resolved peer does not currently have a reachability lease.");
        }

        if (response.LeaseExpiresUnixSeconds is not long leaseSeconds)
        {
            throw new ArgumentException("Reachable peer response is missing a lease expiry.", nameof(response));
        }

        DateTimeOffset leaseExpiresAt;
        try
        {
            leaseExpiresAt = DateTimeOffset.FromUnixTimeSeconds(leaseSeconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentException("Reachability lease expiry is invalid.", nameof(response), exception);
        }

        if (leaseExpiresAt <= (now ?? DateTimeOffset.UtcNow))
        {
            throw new InvalidOperationException("Resolved peer reachability lease has already expired.");
        }

        if (response.Candidates is not { Length: > 0 and <= 8 })
        {
            throw new ArgumentException("Reachable peer must provide between one and eight candidates.", nameof(response));
        }

        var candidates = response.Candidates.ToArray();
        foreach (var candidate in candidates)
        {
            ValidateCandidate(candidate);
        }

        return new PeerConnectionDescriptor(peerId, candidates, leaseExpiresAt);
    }

    public static IReadOnlyList<ConnectionCandidate> SelectCandidates(
        PeerConnectionDescriptor descriptor,
        PeerTransportMode mode)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var candidates = mode == PeerTransportMode.DirectOnly
            ? descriptor.Candidates.Where(candidate => candidate.Kind != "relay").ToArray()
            : descriptor.Candidates.ToArray();

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                mode == PeerTransportMode.DirectOnly
                    ? "No direct candidate is available and relay transport is disabled."
                    : "No connection candidate is available.");
        }

        return candidates;
    }

    private static void ValidateCandidate(ConnectionCandidate candidate)
    {
        if (candidate is null)
        {
            throw new ArgumentException("Connection candidate must not be null.");
        }

        if (candidate.Kind is not ("host" or "srflx" or "relay"))
        {
            throw new ArgumentException("Connection candidate kind is unsupported.");
        }

        if (candidate.Protocol is not ("udp" or "tcp"))
        {
            throw new ArgumentException("Connection candidate protocol is unsupported.");
        }

        if (candidate.Port is < 1 or > 65535 || candidate.Priority < 0)
        {
            throw new ArgumentException("Connection candidate port or priority is invalid.");
        }

        if (!IPAddress.TryParse(candidate.Address, out var address) || IsUnsafeAddress(address))
        {
            throw new ArgumentException("Connection candidate address is invalid or unsafe.");
        }
    }

    private static bool IsUnsafeAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.Broadcast))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6Multicast;
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] is >= 224 and <= 239;
    }
}
