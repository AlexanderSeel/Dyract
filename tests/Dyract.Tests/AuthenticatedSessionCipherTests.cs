using System.Security.Cryptography;
using System.Text;
using Dyract.Crypto.Identity;
using Dyract.Crypto.Session;
using Xunit;

namespace Dyract.Tests;

public sealed class AuthenticatedSessionCipherTests
{
    private const string SessionId = "abcdef0123456789abcdef0123456789";

    [Fact]
    public void BidirectionalFrames_RoundTripWithDirectionalKeys()
    {
        var pair = CreateSessionPair();
        using var initiatorKeys = pair.InitiatorKeys;
        using var responderKeys = pair.ResponderKeys;
        using var initiator = new AuthenticatedSessionCipher(initiatorKeys);
        using var responder = new AuthenticatedSessionCipher(responderKeys);

        var first = initiator.Encrypt("hello bob"u8);
        var second = responder.Encrypt("hello alice"u8);

        Assert.Equal("hello bob", Encoding.UTF8.GetString(responder.Decrypt(first)));
        Assert.Equal("hello alice", Encoding.UTF8.GetString(initiator.Decrypt(second)));
    }

    [Fact]
    public void Replay_IsRejectedByReceiveSequence()
    {
        var pair = CreateSessionPair();
        using var initiatorKeys = pair.InitiatorKeys;
        using var responderKeys = pair.ResponderKeys;
        using var initiator = new AuthenticatedSessionCipher(initiatorKeys);
        using var responder = new AuthenticatedSessionCipher(responderKeys);

        var frame = initiator.Encrypt("once"u8);
        Assert.Equal("once", Encoding.UTF8.GetString(responder.Decrypt(frame)));

        Assert.Throws<CryptographicException>(() => responder.Decrypt(frame));
    }

    [Fact]
    public void OutOfOrderFrame_IsRejectedWithoutAdvancingSequence()
    {
        var pair = CreateSessionPair();
        using var initiatorKeys = pair.InitiatorKeys;
        using var responderKeys = pair.ResponderKeys;
        using var initiator = new AuthenticatedSessionCipher(initiatorKeys);
        using var responder = new AuthenticatedSessionCipher(responderKeys);

        var first = initiator.Encrypt("first"u8);
        var second = initiator.Encrypt("second"u8);

        Assert.Throws<CryptographicException>(() => responder.Decrypt(second));
        Assert.Equal("first", Encoding.UTF8.GetString(responder.Decrypt(first)));
        Assert.Equal("second", Encoding.UTF8.GetString(responder.Decrypt(second)));
    }

    [Fact]
    public void TamperedCiphertextOrTag_IsRejected()
    {
        var pair = CreateSessionPair();
        using var initiatorKeys = pair.InitiatorKeys;
        using var responderKeys = pair.ResponderKeys;
        using var initiator = new AuthenticatedSessionCipher(initiatorKeys);
        using var responder = new AuthenticatedSessionCipher(responderKeys);

        var frame = initiator.Encrypt("authenticated"u8);
        frame[^1] ^= 0x80;

        Assert.Throws<AuthenticationTagMismatchException>(() => responder.Decrypt(frame));
    }

    [Fact]
    public void FrameFromDifferentSession_IsRejected()
    {
        var firstPair = CreateSessionPair();
        var secondPair = CreateSessionPair();
        using var firstInitiatorKeys = firstPair.InitiatorKeys;
        using var firstResponderKeys = firstPair.ResponderKeys;
        using var secondInitiatorKeys = secondPair.InitiatorKeys;
        using var secondResponderKeys = secondPair.ResponderKeys;
        using var firstInitiator = new AuthenticatedSessionCipher(firstInitiatorKeys);
        using var secondResponder = new AuthenticatedSessionCipher(secondResponderKeys);

        var frame = firstInitiator.Encrypt("session scoped"u8);

        Assert.Throws<AuthenticationTagMismatchException>(() => secondResponder.Decrypt(frame));
    }

    [Fact]
    public void EmptyAndOversizedPlaintext_AreRejectedBeforeEncryption()
    {
        var pair = CreateSessionPair();
        using var initiatorKeys = pair.InitiatorKeys;
        using var responderKeys = pair.ResponderKeys;
        using var cipher = new AuthenticatedSessionCipher(initiatorKeys);

        Assert.Throws<ArgumentOutOfRangeException>(() => cipher.Encrypt(ReadOnlySpan<byte>.Empty));
        var oversized = new byte[AuthenticatedSessionCipher.MaximumPlaintextBytes + 1];
        Assert.Throws<ArgumentOutOfRangeException>(() => cipher.Encrypt(oversized));
    }

    private static SessionPair CreateSessionPair()
    {
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        using var initiator = AuthenticatedSessionInitiator.Create(
            alice,
            bob.PeerId,
            bob.ExportPublicKey(),
            SessionId);
        var response = AuthenticatedSessionResponder.Accept(
            bob,
            alice.PeerId,
            alice.ExportPublicKey(),
            initiator.HelloPacket,
            SessionId);
        var initiatorKeys = initiator.Complete(response.ResponsePacket);
        return new SessionPair(initiatorKeys, response.Keys);
    }

    private sealed record SessionPair(
        AuthenticatedSessionKeys InitiatorKeys,
        AuthenticatedSessionKeys ResponderKeys);
}
