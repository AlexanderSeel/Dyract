using System.Buffers.Binary;
using Dyract.Core.Identity;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Xunit;

namespace Dyract.Tests;

public sealed class PeerMessagingProtocolTests
{
    private const string MessageId = "019c1a2b3c4d7e8f9123456789abcdef";

    [Fact]
    public void TextMessage_RoundTripsUnicodeAndIdentityScope()
    {
        using var aliceIdentity = PeerIdentity.Generate();
        using var bobIdentity = PeerIdentity.Generate();
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(1_786_110_000_123);
        var original = new PeerTextMessageFrame(
            MessageId,
            aliceIdentity.PeerId,
            bobIdentity.PeerId,
            createdAt,
            "Hello 👋 — labas — こんにちは");

        var encoded = PeerMessagingProtocol.Encode(original);

        Assert.True(PeerMessagingProtocol.TryDecode(encoded, out var decoded, out var error), error);
        var text = Assert.IsType<PeerTextMessageFrame>(decoded);
        Assert.Equal(original, text);
        Assert.True(PeerMessagingProtocol.TryValidateForReceiver(
            text,
            bobIdentity.PeerId,
            aliceIdentity.PeerId,
            createdAt.AddSeconds(1),
            out error), error);
    }

    [Fact]
    public void DeliveryAck_RoundTripsAndReversesPeerDirection()
    {
        using var aliceIdentity = PeerIdentity.Generate();
        using var bobIdentity = PeerIdentity.Generate();
        var createdAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var text = new PeerTextMessageFrame(
            MessageId,
            aliceIdentity.PeerId,
            bobIdentity.PeerId,
            createdAt,
            "hello");
        var ack = PeerMessagingProtocol.CreateDeliveryAck(text, createdAt.AddMilliseconds(250));

        Assert.Equal(bobIdentity.PeerId, ack.SenderPeerId);
        Assert.Equal(aliceIdentity.PeerId, ack.RecipientPeerId);
        Assert.Equal(MessageId, ack.MessageId);

        var encoded = PeerMessagingProtocol.Encode(ack);
        Assert.True(PeerMessagingProtocol.TryDecode(encoded, out var decoded, out var error), error);
        Assert.Equal(ack, Assert.IsType<PeerDeliveryAckFrame>(decoded));
    }

    [Fact]
    public void ReceiverValidation_RejectsCrossPeerAndFutureFrames()
    {
        using var aliceIdentity = PeerIdentity.Generate();
        using var bobIdentity = PeerIdentity.Generate();
        using var malloryIdentity = PeerIdentity.Generate();
        var now = DateTimeOffset.UtcNow;
        var frame = new PeerTextMessageFrame(
            MessageId,
            aliceIdentity.PeerId,
            bobIdentity.PeerId,
            now,
            "hello");

        Assert.False(PeerMessagingProtocol.TryValidateForReceiver(
            frame,
            bobIdentity.PeerId,
            malloryIdentity.PeerId,
            now,
            out _));
        Assert.False(PeerMessagingProtocol.TryValidateForReceiver(
            frame with { CreatedAt = now.AddMinutes(3), Timestamp = now.AddMinutes(3) },
            bobIdentity.PeerId,
            aliceIdentity.PeerId,
            now,
            out _));
    }

    [Fact]
    public void Decode_RejectsPayloadLengthTampering()
    {
        using var aliceIdentity = PeerIdentity.Generate();
        using var bobIdentity = PeerIdentity.Generate();
        var encoded = PeerMessagingProtocol.Encode(new PeerTextMessageFrame(
            MessageId,
            aliceIdentity.PeerId,
            bobIdentity.PeerId,
            DateTimeOffset.UtcNow,
            "hello"));

        const int payloadLengthOffset = 4 + 1 + 1 + 32 + 56 + 56 + 8;
        BinaryPrimitives.WriteUInt32BigEndian(encoded.AsSpan(payloadLengthOffset, 4), 1234);

        Assert.False(PeerMessagingProtocol.TryDecode(encoded, out _, out var error));
        Assert.Contains("payload length", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decode_RejectsInvalidUtf8()
    {
        using var aliceIdentity = PeerIdentity.Generate();
        using var bobIdentity = PeerIdentity.Generate();
        var encoded = PeerMessagingProtocol.Encode(new PeerTextMessageFrame(
            MessageId,
            aliceIdentity.PeerId,
            bobIdentity.PeerId,
            DateTimeOffset.UtcNow,
            "x"));
        encoded[^1] = 0xff;

        Assert.False(PeerMessagingProtocol.TryDecode(encoded, out _, out var error));
        Assert.Contains("UTF-8", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Encode_RejectsNonCanonicalMessageIdAndSelfRecipient()
    {
        using var aliceIdentity = PeerIdentity.Generate();
        using var bobIdentity = PeerIdentity.Generate();

        Assert.Throws<ArgumentException>(() => PeerMessagingProtocol.Encode(new PeerTextMessageFrame(
            MessageId.ToUpperInvariant(),
            aliceIdentity.PeerId,
            bobIdentity.PeerId,
            DateTimeOffset.UtcNow,
            "hello")));

        Assert.Throws<ArgumentException>(() => PeerMessagingProtocol.Encode(new PeerTextMessageFrame(
            MessageId,
            aliceIdentity.PeerId,
            aliceIdentity.PeerId,
            DateTimeOffset.UtcNow,
            "hello")));
    }
}
