using System.Text;
using Dyract.Protocol;
using Xunit;

namespace Dyract.Tests;

public sealed class PresenceProofTests
{
    [Fact]
    public void PresenceProof_BindsCandidatesLeaseTimestampAndNonce()
    {
        var candidates = new[]
        {
            new ConnectionCandidate("host", "udp", "192.168.1.20", 45000, 100),
            new ConnectionCandidate("srflx", "udp", "203.0.113.10", 52000, 90)
        };

        var payload = ProofPayload.ForPresence(
            "dyr_peer",
            candidates,
            200,
            100,
            "nonce");

        Assert.Equal(
            "dyract:presence:v1\ndyr_peer\n200\n100\nnonce\n2\nhost\tudp\t192.168.1.20\t45000\t100\nsrflx\tudp\t203.0.113.10\t52000\t90",
            Encoding.UTF8.GetString(payload));
    }

    [Fact]
    public void ResolveProof_BindsCapabilityToRequesterAndTarget()
    {
        var payload = ProofPayload.ForResolve(
            "dyr_requester",
            "dyr_target",
            "0123456789abcdef0123456789abcdef",
            123,
            "nonce");

        Assert.Equal(
            "dyract:resolve:v1\ndyr_requester\ndyr_target\n0123456789abcdef0123456789abcdef\n123\nnonce",
            Encoding.UTF8.GetString(payload));
    }

    [Fact]
    public void PresenceProof_RejectsStructuredDelimiterInjection()
    {
        var candidates = new[]
        {
            new ConnectionCandidate("host\tspoof", "udp", "192.168.1.20", 45000, 100)
        };

        Assert.Throws<ArgumentException>(() =>
            ProofPayload.ForPresence("dyr_peer", candidates, 200, 100, "nonce"));
    }
}
