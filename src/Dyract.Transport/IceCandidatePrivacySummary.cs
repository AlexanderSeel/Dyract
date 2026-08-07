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

        var typeIndex = -1;
        for (var index = 6; index < tokens.Length - 1; index++)
        {
            if (string.Equals(tokens[index], "typ", StringComparison.OrdinalIgnoreCase))
            {
                typeIndex = index;
                break;
            }
        }

        return typeIndex >= 0 && TryCreate(tokens[typeIndex + 1], tokens[2], out summary);
    }

    public static bool TryCreate(
        string? candidateType,
        string? transport,
        out IceCandidatePrivacySummary summary)
    {
        summary = default;
        if (string.IsNullOrWhiteSpace(candidateType) || string.IsNullOrWhiteSpace(transport))
        {
            return false;
        }

        summary = new IceCandidatePrivacySummary(
            ParseCategory(candidateType),
            ParseTransport(transport));
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

public sealed record SelectedIcePathPrivacySummary(
    IceCandidatePrivacySummary Local,
    IceCandidatePrivacySummary Remote)
{
    public string DisplayValue => $"{Local.DisplayValue} -> {Remote.DisplayValue}";
}
