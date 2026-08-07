using System.Text;
using Dyract.Protocol;
using Xunit;

namespace Dyract.Tests;

public sealed class ProofPayloadTests
{
    [Fact]
    public void RegistrationProof_IsVersionedAndDeterministic()
    {
        var payload = ProofPayload.ForRegistration(
            "challenge-id",
            "dyr_example",
            "public-key",
            "challenge");

        Assert.Equal(
            "dyract:register:v1\nchallenge-id\ndyr_example\npublic-key\nchallenge",
            Encoding.UTF8.GetString(payload));
    }

    [Fact]
    public void LookupProof_IsVersionedAndBindsTargetTimestampAndNonce()
    {
        var payload = ProofPayload.ForLookup(
            "dyr_requester",
            "dyr_target",
            1234567890,
            "nonce");

        Assert.Equal(
            "dyract:lookup:v1\ndyr_requester\ndyr_target\n1234567890\nnonce",
            Encoding.UTF8.GetString(payload));
    }

    [Fact]
    public void ProofFields_RejectLineBreakInjection()
    {
        Assert.Throws<ArgumentException>(() =>
            ProofPayload.ForLookup("requester\nspoof", "target", 1, "nonce"));
    }
}
