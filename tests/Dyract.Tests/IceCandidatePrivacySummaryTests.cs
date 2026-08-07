using Dyract.Transport;
using Xunit;

namespace Dyract.Tests;

public sealed class IceCandidatePrivacySummaryTests
{
    [Theory]
    [InlineData("candidate:1 1 udp 2122260223 192.168.1.20 54321 typ host generation 0", IceCandidateCategory.Host, IceTransportCategory.Udp, "host/udp")]
    [InlineData("candidate:2 1 UDP 1686052607 203.0.113.8 62000 typ srflx raddr 192.168.1.20 rport 54321", IceCandidateCategory.ServerReflexive, IceTransportCategory.Udp, "srflx/udp")]
    [InlineData("candidate:3 1 tcp 1518280447 192.0.2.10 9 typ prflx tcptype active", IceCandidateCategory.PeerReflexive, IceTransportCategory.Tcp, "prflx/tcp")]
    [InlineData("candidate:4 1 udp 1677734911 198.51.100.12 50000 typ relay raddr 203.0.113.8 rport 62000", IceCandidateCategory.Relay, IceTransportCategory.Udp, "relay/udp")]
    public void TryParse_ReturnsOnlyPrivacySafeClassification(
        string candidate,
        IceCandidateCategory expectedCategory,
        IceTransportCategory expectedTransport,
        string expectedDisplay)
    {
        Assert.True(IceCandidatePrivacySummary.TryParse(candidate, out var summary));
        Assert.Equal(expectedCategory, summary.Category);
        Assert.Equal(expectedTransport, summary.Transport);
        Assert.Equal(expectedDisplay, summary.DisplayValue);
        Assert.DoesNotContain("192.", summary.DisplayValue, StringComparison.Ordinal);
        Assert.DoesNotContain("203.", summary.DisplayValue, StringComparison.Ordinal);
        Assert.DoesNotContain("198.", summary.DisplayValue, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("candidate:broken")]
    [InlineData("candidate:1 1 udp 1 192.168.1.1 5000 generation 0")]
    public void TryParse_RejectsMalformedCandidate(string? candidate)
    {
        Assert.False(IceCandidatePrivacySummary.TryParse(candidate, out _));
    }

    [Fact]
    public void TryParse_UnknownTypeAndTransport_DoNotExposeOriginalValues()
    {
        const string candidate = "candidate:5 1 quic 123 10.10.10.10 9999 typ futuretype generation 0";

        Assert.True(IceCandidatePrivacySummary.TryParse(candidate, out var summary));
        Assert.Equal(IceCandidateCategory.Unknown, summary.Category);
        Assert.Equal(IceTransportCategory.Unknown, summary.Transport);
        Assert.Equal("unknown/unknown", summary.DisplayValue);
    }

    [Theory]
    [InlineData("host", "udp", IceCandidateCategory.Host, IceTransportCategory.Udp, "host/udp")]
    [InlineData("srflx", "TCP", IceCandidateCategory.ServerReflexive, IceTransportCategory.Tcp, "srflx/tcp")]
    public void TryCreate_ClassifiesKnownStatsTokens(
        string candidateType,
        string transport,
        IceCandidateCategory expectedCategory,
        IceTransportCategory expectedTransport,
        string expectedDisplay)
    {
        Assert.True(IceCandidatePrivacySummary.TryCreate(candidateType, transport, out var summary));
        Assert.Equal(expectedCategory, summary.Category);
        Assert.Equal(expectedTransport, summary.Transport);
        Assert.Equal(expectedDisplay, summary.DisplayValue);
    }

    [Fact]
    public void TryCreate_UnknownStatsTokens_AreNotEchoed()
    {
        Assert.True(IceCandidatePrivacySummary.TryCreate(
            "future-candidate",
            "quic",
            out var summary));

        Assert.Equal(IceCandidateCategory.Unknown, summary.Category);
        Assert.Equal(IceTransportCategory.Unknown, summary.Transport);
        Assert.Equal("unknown/unknown", summary.DisplayValue);
        Assert.DoesNotContain("future-candidate", summary.DisplayValue, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quic", summary.DisplayValue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectedPath_DisplayContainsOnlySafeSummaries()
    {
        var selected = new SelectedIcePathPrivacySummary(
            new IceCandidatePrivacySummary(IceCandidateCategory.Host, IceTransportCategory.Udp),
            new IceCandidatePrivacySummary(IceCandidateCategory.ServerReflexive, IceTransportCategory.Udp));

        Assert.Equal("host/udp -> srflx/udp", selected.DisplayValue);
    }
}
