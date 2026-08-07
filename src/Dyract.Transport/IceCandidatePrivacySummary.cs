namespace Dyract.Transport;

public enum IceCandidateCategory
{
    Unknown = 0,
    Host,
    ServerReflexive,
    PeerReflexive,
    Relay
}

public enum IceTransportCategory
{
    Unknown = 0,
    Udp,
    Tcp
}

public readonly record struct IceCandidatePrivacySummary(
    IceCandidateCategory Category,
    IceTransportCategory Transport)
{
    public string DisplayValue => $"{CategoryToDisplay(Category)}/{TransportToDisplay(Transport)}";

    public static bool TryParse(string? candidate, out IceCandidatePrivacySummary summary)
    {
        summary = default;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var tokens = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 8)
        {
            return false;
        }

        var transport = ParseTransport(tokens[2]);
        var typeIndex = -1;
        for (var index = 6; index < tokens.Length - 1; index++)
        {
            if (string.Equals(tokens[index], "typ", StringComparison.OrdinalIgnoreCase))
            {
                typeIndex = index;
                break;
            }
        }

        if (typeIndex < 0)
        {
            return false;
        }

        summary = new IceCandidatePrivacySummary(
            ParseCategory(tokens[typeIndex + 1]),
            transport);
        return true;
    }

    private static IceCandidateCategory ParseCategory(string value)
        => value.ToLowerInvariant() switch
        {
            "host" => IceCandidateCategory.Host,
            "srflx" => IceCandidateCategory.ServerReflexive,
            "prflx" => IceCandidateCategory.PeerReflexive,
            "relay" => IceCandidateCategory.Relay,
            _ => IceCandidateCategory.Unknown
        };

    private static IceTransportCategory ParseTransport(string value)
        => value.ToLowerInvariant() switch
        {
            "udp" => IceTransportCategory.Udp,
            "tcp" => IceTransportCategory.Tcp,
            _ => IceTransportCategory.Unknown
        };

    private static string CategoryToDisplay(IceCandidateCategory category)
        => category switch
        {
            IceCandidateCategory.Host => "host",
            IceCandidateCategory.ServerReflexive => "srflx",
            IceCandidateCategory.PeerReflexive => "prflx",
            IceCandidateCategory.Relay => "relay",
            _ => "unknown"
        };

    private static string TransportToDisplay(IceTransportCategory transport)
        => transport switch
        {
            IceTransportCategory.Udp => "udp",
            IceTransportCategory.Tcp => "tcp",
            _ => "unknown"
        };
}
