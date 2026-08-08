using System.Security.Cryptography;
using System.Text;
using Dyract.Crypto.Identity;
using Dyract.Crypto.Session;
using Dyract.Protocol;
using Xunit;

namespace Dyract.Tests;

public sealed class ProtocolFuzzPropertyTests
{
    private const string SessionA = "11111111111111111111111111111111";
    private const string SessionB = "22222222222222222222222222222222";

    [Fact]
    public void PeerMessaging_ValidGeneratedFramesRoundTripCanonically()
    {
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        var random = new Random(0xD1A7);
        var epoch = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);

        for (var index = 0; index < 256; index++)
        {
            PeerApplicationFrame original = index % 4 == 0
                ? new PeerDeliveryAckFrame(
                    index.ToString("x32"),
                    bob.PeerId,
                    alice.PeerId,
                    epoch.AddMilliseconds(index))
                : new PeerTextMessageFrame(
                    index.ToString("x32"),
                    alice.PeerId,
                    bob.PeerId,
                    epoch.AddMilliseconds(index),
                    CreateGeneratedText(random, index));

            var encoded = PeerMessagingProtocol.Encode(original);

            Assert.True(PeerMessagingProtocol.TryDecode(encoded, out var decoded, out var error), error);
            Assert.Equal(original, decoded);
            Assert.Equal(encoded, PeerMessagingProtocol.Encode(decoded!));
        }
    }

    [Fact]
    public void PeerMessaging_MutatedValidFramesAreRejectedOrRemainCanonical()
    {
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        var original = PeerMessagingProtocol.Encode(new PeerTextMessageFrame(
            "0123456789abcdef0123456789abcdef",
            alice.PeerId,
            bob.PeerId,
            DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_123),
            "mutation corpus 👋 labas こんにちは — authenticated application payload"));

        for (var offset = 0; offset < original.Length; offset++)
        {
            var mutated = original.ToArray();
            mutated[offset] ^= (byte)(1 << (offset & 7));

            if (PeerMessagingProtocol.TryDecode(mutated, out var decoded, out var error))
            {
                Assert.NotNull(decoded);
                Assert.Equal(mutated, PeerMessagingProtocol.Encode(decoded));
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(error));
            }
        }
    }

    [Fact]
    public void PeerMessaging_DeterministicGarbageCorpusNeverEscapesDecoderContract()
    {
        var random = new Random(0x51A17);
        var boundaryLengths = new[] { 0, 1, 5, 31, 64, 127, 166, 167, 168, 255, 512, 1024, 2048, 4096 };

        foreach (var length in boundaryLengths)
        {
            ExerciseGarbagePacket(new byte[length]);
        }

        for (var index = 0; index < 512; index++)
        {
            var bytes = new byte[random.Next(0, 4097)];
            random.NextBytes(bytes);
            ExerciseGarbagePacket(bytes);
        }
    }

    [Fact]
    public void Handshake_SampledSingleByteMutationsCannotAuthenticate()
    {
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        using var initiator = AuthenticatedSessionInitiator.Create(
            alice,
            bob.PeerId,
            bob.ExportPublicKey(),
            SessionA);

        var hello = initiator.HelloPacket;
        var sampleCount = Math.Min(96, hello.Length);

        for (var sample = 0; sample < sampleCount; sample++)
        {
            var offset = sample * (hello.Length - 1) / Math.Max(1, sampleCount - 1);
            var mutated = hello.ToArray();
            mutated[offset] ^= (byte)(1 << (sample & 7));

            AssertHandshakeRejected(() =>
            {
                var result = AuthenticatedSessionResponder.Accept(
                    bob,
                    alice.PeerId,
                    alice.ExportPublicKey(),
                    mutated,
                    SessionA);
                result.Keys.Dispose();
            });
        }
    }

    [Fact]
    public void Handshake_DowngradeAndCrossSessionResponsesAreRejected()
    {
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        var alicePublicKey = alice.ExportPublicKey();
        var bobPublicKey = bob.ExportPublicKey();

        using (var downgradeInitiator = AuthenticatedSessionInitiator.Create(
                   alice,
                   bob.PeerId,
                   bobPublicKey,
                   SessionA))
        {
            var downgradedHello = downgradeInitiator.HelloPacket.ToArray();
            downgradedHello[4] = 0;
            AssertHandshakeRejected(() =>
            {
                var result = AuthenticatedSessionResponder.Accept(
                    bob,
                    alice.PeerId,
                    alicePublicKey,
                    downgradedHello,
                    SessionA);
                result.Keys.Dispose();
            });
        }

        using (var responseInitiator = AuthenticatedSessionInitiator.Create(
                   alice,
                   bob.PeerId,
                   bobPublicKey,
                   SessionA))
        {
            var response = AuthenticatedSessionResponder.Accept(
                bob,
                alice.PeerId,
                alicePublicKey,
                responseInitiator.HelloPacket,
                SessionA);
            using var responderKeys = response.Keys;
            var downgradedResponse = response.ResponsePacket.ToArray();
            downgradedResponse[4] = 0;
            Assert.Throws<CryptographicException>(() => responseInitiator.Complete(downgradedResponse));
        }

        using var initiatorA = AuthenticatedSessionInitiator.Create(
            alice,
            bob.PeerId,
            bobPublicKey,
            SessionA);
        using var initiatorB = AuthenticatedSessionInitiator.Create(
            alice,
            bob.PeerId,
            bobPublicKey,
            SessionB);
        var responseA = AuthenticatedSessionResponder.Accept(
            bob,
            alice.PeerId,
            alicePublicKey,
            initiatorA.HelloPacket,
            SessionA);
        using var responseAKeys = responseA.Keys;

        Assert.Throws<CryptographicException>(() => initiatorB.Complete(responseA.ResponsePacket));
    }

    [Fact]
    public void EncryptedSession_MutationFailuresNeverAdvanceReceiveSequence()
    {
        var pair = CreateSessionPair(SessionA);
        using var initiatorKeys = pair.InitiatorKeys;
        using var responderKeys = pair.ResponderKeys;
        using var sender = new AuthenticatedSessionCipher(initiatorKeys);
        using var receiver = new AuthenticatedSessionCipher(responderKeys);

        for (var index = 0; index < 64; index++)
        {
            var plaintext = Encoding.UTF8.GetBytes($"frame-{index:D3}-mutation-property-👋");
            var frame = sender.Encrypt(plaintext);
            var mutated = frame.ToArray();
            var offset = index * (frame.Length - 1) / 63;
            mutated[offset] ^= (byte)(1 << (index & 7));

            Assert.ThrowsAny<CryptographicException>(() => receiver.Decrypt(mutated));
            Assert.Equal(plaintext, receiver.Decrypt(frame));
        }

        var truncatedPlaintext = "truncated-frame"u8.ToArray();
        var truncatedFrame = sender.Encrypt(truncatedPlaintext);
        Assert.Throws<CryptographicException>(() => receiver.Decrypt(truncatedFrame[..^1]));
        Assert.Equal(truncatedPlaintext, receiver.Decrypt(truncatedFrame));

        var extendedPlaintext = "extended-frame"u8.ToArray();
        var extendedFrame = sender.Encrypt(extendedPlaintext);
        var extended = new byte[extendedFrame.Length + 1];
        extendedFrame.CopyTo(extended, 0);
        extended[^1] = 0x42;
        Assert.Throws<CryptographicException>(() => receiver.Decrypt(extended));
        Assert.Equal(extendedPlaintext, receiver.Decrypt(extendedFrame));
    }

    [Fact]
    public void EncryptedSession_ReplayAndCrossSessionFramesStayIsolated()
    {
        var firstPair = CreateSessionPair(SessionA);
        var secondPair = CreateSessionPair(SessionB);
        using var firstInitiatorKeys = firstPair.InitiatorKeys;
        using var firstResponderKeys = firstPair.ResponderKeys;
        using var secondInitiatorKeys = secondPair.InitiatorKeys;
        using var secondResponderKeys = secondPair.ResponderKeys;
        using var firstSender = new AuthenticatedSessionCipher(firstInitiatorKeys);
        using var firstReceiver = new AuthenticatedSessionCipher(firstResponderKeys);
        using var secondReceiver = new AuthenticatedSessionCipher(secondResponderKeys);

        var frame = firstSender.Encrypt("session-isolated"u8);

        Assert.ThrowsAny<CryptographicException>(() => secondReceiver.Decrypt(frame));
        Assert.Equal("session-isolated", Encoding.UTF8.GetString(firstReceiver.Decrypt(frame)));
        Assert.Throws<CryptographicException>(() => firstReceiver.Decrypt(frame));
    }

    private static void ExerciseGarbagePacket(byte[] packet)
    {
        if (PeerMessagingProtocol.TryDecode(packet, out var decoded, out var error))
        {
            Assert.NotNull(decoded);
            Assert.Equal(packet, PeerMessagingProtocol.Encode(decoded));
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    private static void AssertHandshakeRejected(Action action)
    {
        var rejected = false;
        try
        {
            action();
        }
        catch (CryptographicException)
        {
            rejected = true;
        }
        catch (ArgumentException)
        {
            rejected = true;
        }

        Assert.True(rejected, "Mutated session handshake data was unexpectedly accepted.");
    }

    private static string CreateGeneratedText(Random random, int index)
    {
        string[] atoms = ["a", "Z", "7", " ", "labas", "こんにちは", "👋", "—", "é", "ß", "\n"];
        var builder = new StringBuilder($"message-{index:D3}-");
        var atomCount = random.Next(1, 48);
        for (var item = 0; item < atomCount; item++)
        {
            builder.Append(atoms[random.Next(atoms.Length)]);
        }

        return builder.ToString();
    }

    private static SessionPair CreateSessionPair(string sessionId)
    {
        using var alice = PeerIdentity.Generate();
        using var bob = PeerIdentity.Generate();
        using var initiator = AuthenticatedSessionInitiator.Create(
            alice,
            bob.PeerId,
            bob.ExportPublicKey(),
            sessionId);
        var response = AuthenticatedSessionResponder.Accept(
            bob,
            alice.PeerId,
            alice.ExportPublicKey(),
            initiator.HelloPacket,
            sessionId);
        var initiatorKeys = initiator.Complete(response.ResponsePacket);
        return new SessionPair(initiatorKeys, response.Keys);
    }

    private sealed record SessionPair(
        AuthenticatedSessionKeys InitiatorKeys,
        AuthenticatedSessionKeys ResponderKeys);
}
