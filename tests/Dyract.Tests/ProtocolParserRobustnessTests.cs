using System.Security.Cryptography;
using System.Text;
using Dyract.Core.Identity;
using Dyract.Crypto.Identity;
using Dyract.Crypto.Session;
using Dyract.Protocol;
using Xunit;

namespace Dyract.Tests;

public sealed class ProtocolParserRobustnessTests
{
    private const string ContactPrefix = "dyract://contact/v1/";
    private const string PairingPrefix = "dyract://pair/v1/";
    private const string SessionId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void ContactInvitation_RoundTripRemainsValid()
    {
        using var identity = PeerIdentity.Generate();
        var invitation = new ContactInvitation(
            1,
            identity.PeerId.Value,
            Convert.ToBase64String(identity.ExportPublicKey()),
            1_800_000_000);

        var encoded = ContactInvitationCodec.Encode(invitation);

        Assert.True(ContactInvitationCodec.TryDecode(encoded, out var decoded, out var error), error);
        Assert.Equal(invitation, decoded);
    }

    [Fact]
    public void PairingResponse_RoundTripRemainsStructurallyValid()
    {
        using var issuer = PeerIdentity.Generate();
        using var grantee = PeerIdentity.Generate();
        var capability = new ContactCapability(
            1,
            issuer.PeerId.Value,
            grantee.PeerId.Value,
            "0123456789abcdef0123456789abcdef",
            1_800_000_000,
            1_800_003_600,
            Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }));

        var encoded = ContactPairingCodec.Encode(capability);

        Assert.True(ContactPairingCodec.TryDecode(encoded, out var decoded, out var error), error);
        Assert.Equal(capability, decoded);
    }

    [Fact]
    public void ContactInvitation_OversizedEncodedPayloadIsRejected()
    {
        var oversized = ContactPrefix + new string('A', 20_000);

        Assert.False(ContactInvitationCodec.TryDecode(oversized, out var invitation, out var error));
        Assert.Null(invitation);
        Assert.NotNull(error);
    }

    [Fact]
    public void PairingResponse_OversizedEncodedPayloadIsRejected()
    {
        var oversized = PairingPrefix + new string('A', 20_000);

        Assert.False(ContactPairingCodec.TryDecode(oversized, out var capability, out var error));
        Assert.Null(capability);
        Assert.NotNull(error);
    }

    [Fact]
    public void ContactAndPairingCodecs_DoNotCrossAcceptProtocolDomains()
    {
        using var identity = PeerIdentity.Generate();
        var invitation = ContactInvitationCodec.Encode(new ContactInvitation(
            1,
            identity.PeerId.Value,
            Convert.ToBase64String(identity.ExportPublicKey()),
            1_800_000_000));

        Assert.False(ContactPairingCodec.TryDecode(invitation, out _, out _));
        Assert.False(ContactInvitationCodec.TryDecode(PairingPrefix + invitation[ContactPrefix.Length..], out _, out _));
    }

    [Fact]
    public void QrCodecs_RandomMalformedInputsNeverThrow()
    {
        var random = new Random(0x44595241);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_:/?&=%{}[]!@#$^*()\\\"'\r\n\t";

        for (var iteration = 0; iteration < 5_000; iteration++)
        {
            var length = random.Next(0, 16_384);
            var builder = new StringBuilder(length + ContactPrefix.Length);

            var mode = random.Next(4);
            if (mode == 0)
            {
                builder.Append(ContactPrefix);
            }
            else if (mode == 1)
            {
                builder.Append(PairingPrefix);
            }

            for (var index = builder.Length; index < length; index++)
            {
                builder.Append(alphabet[random.Next(alphabet.Length)]);
            }

            var input = builder.ToString();
            _ = ContactInvitationCodec.TryDecode(input, out _, out _);
            _ = ContactPairingCodec.TryDecode(input, out _, out _);
        }
    }

    [Fact]
    public void QrCodecs_MutatedValidPayloadsNeverThrow()
    {
        using var identity = PeerIdentity.Generate();
        var valid = ContactInvitationCodec.Encode(new ContactInvitation(
            1,
            identity.PeerId.Value,
            Convert.ToBase64String(identity.ExportPublicKey()),
            1_800_000_000));
        var random = new Random(0x50415253);
        const string mutations = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

        for (var iteration = 0; iteration < 2_000; iteration++)
        {
            var chars = valid.ToCharArray();
            var mutationCount = random.Next(1, Math.Min(8, chars.Length));
            for (var mutation = 0; mutation < mutationCount; mutation++)
            {
                var index = random.Next(chars.Length);
                chars[index] = mutations[random.Next(mutations.Length)];
            }

            _ = ContactInvitationCodec.TryDecode(new string(chars), out _, out _);
        }
    }

    [Fact]
    public void SessionHandshake_RandomBinaryInputsFailClosedWithoutUnexpectedExceptions()
    {
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        var alicePublicKey = alice.ExportPublicKey();
        var random = new Random(0x53455353);

        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            var bytes = new byte[random.Next(0, 40_000)];
            random.NextBytes(bytes);

            try
            {
                var accepted = AuthenticatedSessionResponder.Accept(
                    bob,
                    alice.PeerId,
                    alicePublicKey,
                    bytes,
                    SessionId);
                accepted.Keys.Dispose();
                Assert.Fail("Random session handshake bytes were unexpectedly authenticated.");
            }
            catch (CryptographicException)
            {
                // Expected fail-closed parser/authentication result.
            }
            catch (ArgumentException)
            {
                // Expected bounds/format rejection.
            }
        }
    }
}
