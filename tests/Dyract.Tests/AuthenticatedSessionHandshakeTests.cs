using System.Security.Cryptography;
using Dyract.Crypto.Identity;
using Dyract.Crypto.Session;
using Xunit;

namespace Dyract.Tests;

public sealed class AuthenticatedSessionHandshakeTests
{
    private const string SessionId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void SignedEphemeralHandshake_DerivesOppositeDirectionalKeys()
    {
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        var alicePublicKey = alice.ExportPublicKey();
        var bobPublicKey = bob.ExportPublicKey();

        using var initiator = AuthenticatedSessionInitiator.Create(
            alice,
            bob.PeerId,
            bobPublicKey,
            SessionId);
        var response = AuthenticatedSessionResponder.Accept(
            bob,
            alice.PeerId,
            alicePublicKey,
            initiator.HelloPacket,
            SessionId);
        using var responderKeys = response.Keys;
        using var initiatorKeys = initiator.Complete(response.ResponsePacket);

        Assert.Equal(initiatorKeys.ExportSendKey(), responderKeys.ExportReceiveKey());
        Assert.Equal(initiatorKeys.ExportReceiveKey(), responderKeys.ExportSendKey());
        Assert.Equal(initiatorKeys.ExportTranscriptHash(), responderKeys.ExportTranscriptHash());
        Assert.False(initiatorKeys.ExportSendKey().SequenceEqual(initiatorKeys.ExportReceiveKey()));
    }

    [Fact]
    public void Initiator_RejectsPinnedIdentityKeyForDifferentPeer()
    {
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        using var mallory = PeerIdentity.Generate();

        Assert.Throws<CryptographicException>(() =>
            AuthenticatedSessionInitiator.Create(
                alice,
                bob.PeerId,
                mallory.ExportPublicKey(),
                SessionId));
    }

    [Fact]
    public void Responder_RejectsTamperedHelloSignature()
    {
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        using var initiator = AuthenticatedSessionInitiator.Create(
            alice,
            bob.PeerId,
            bob.ExportPublicKey(),
            SessionId);
        var tampered = initiator.HelloPacket.ToArray();
        tampered[^1] ^= 0x01;

        Assert.Throws<CryptographicException>(() =>
            AuthenticatedSessionResponder.Accept(
                bob,
                alice.PeerId,
                alice.ExportPublicKey(),
                tampered,
                SessionId));
    }

    [Fact]
    public void Initiator_RejectsTamperedResponseSignature()
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
        using var responderKeys = response.Keys;
        var tampered = response.ResponsePacket.ToArray();
        tampered[^1] ^= 0x01;

        Assert.Throws<CryptographicException>(() => initiator.Complete(tampered));
    }

    [Fact]
    public void ResponseForDifferentHello_IsRejectedByTranscriptBinding()
    {
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        var bobPublicKey = bob.ExportPublicKey();
        var alicePublicKey = alice.ExportPublicKey();

        using var first = AuthenticatedSessionInitiator.Create(
            alice,
            bob.PeerId,
            bobPublicKey,
            SessionId);
        using var second = AuthenticatedSessionInitiator.Create(
            alice,
            bob.PeerId,
            bobPublicKey,
            SessionId);
        var response = AuthenticatedSessionResponder.Accept(
            bob,
            alice.PeerId,
            alicePublicKey,
            first.HelloPacket,
            SessionId);
        using var responderKeys = response.Keys;

        Assert.Throws<CryptographicException>(() => second.Complete(response.ResponsePacket));
    }

    [Fact]
    public void Responder_RejectsWrongExpectedSession()
    {
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        using var initiator = AuthenticatedSessionInitiator.Create(
            alice,
            bob.PeerId,
            bob.ExportPublicKey(),
            SessionId);

        Assert.Throws<CryptographicException>(() =>
            AuthenticatedSessionResponder.Accept(
                bob,
                alice.PeerId,
                alice.ExportPublicKey(),
                initiator.HelloPacket,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
    }
}
