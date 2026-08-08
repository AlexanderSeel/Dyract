using System.Buffers.Binary;
using System.Text;
using Dyract.Core.Identity;

namespace Dyract.Protocol;

public abstract record PeerApplicationFrame(
    string MessageId,
    PeerId SenderPeerId,
    PeerId RecipientPeerId)
{
    public abstract DateTimeOffset Timestamp { get; }
}

public sealed record PeerTextMessageFrame(
    string MessageId,
    PeerId SenderPeerId,
    PeerId RecipientPeerId,
    DateTimeOffset CreatedAt,
    string Text)
    : PeerApplicationFrame(MessageId, SenderPeerId, RecipientPeerId)
{
    public override DateTimeOffset Timestamp => CreatedAt;
}

public sealed record PeerDeliveryAckFrame(
    string MessageId,
    PeerId SenderPeerId,
    PeerId RecipientPeerId,
    DateTimeOffset DeliveredAt)
    : PeerApplicationFrame(MessageId, SenderPeerId, RecipientPeerId)
{
    public override DateTimeOffset Timestamp => DeliveredAt;
}

public sealed record PeerReadAckFrame(
    string MessageId,
    PeerId SenderPeerId,
    PeerId RecipientPeerId,
    DateTimeOffset ReadAt)
    : PeerApplicationFrame(MessageId, SenderPeerId, RecipientPeerId)
{
    public override DateTimeOffset Timestamp => ReadAt;
}

public static class PeerMessagingProtocol
{
    private static readonly byte[] Magic = "DYRM"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const byte Version = 1;
    private const byte TextFrameType = 1;
    private const byte DeliveryAckFrameType = 2;
    private const byte ReadAckFrameType = 3;
    private const int MessageIdLength = 32;
    private const int PeerIdLength = 56;
    private const int HeaderLength = 4 + 1 + 1 + MessageIdLength + (PeerIdLength * 2) + 8 + 4;
    private const int MaximumTextCharacters = 32_768;
    private const int MaximumTextUtf8Bytes = 128 * 1024;
    private const int MaximumFrameBytes = HeaderLength + MaximumTextUtf8Bytes;

    public static byte[] Encode(PeerApplicationFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateCommon(frame.MessageId, frame.SenderPeerId, frame.RecipientPeerId, frame.Timestamp);

        return frame switch
        {
            PeerTextMessageFrame text => EncodeText(text),
            PeerDeliveryAckFrame ack => EncodeDeliveryAck(ack),
            PeerReadAckFrame read => EncodeReadAck(read),
            _ => throw new NotSupportedException($"Peer frame type {frame.GetType().Name} is not supported.")
        };
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> packet,
        out PeerApplicationFrame? frame,
        out string? error)
    {
        frame = null;
        error = null;

        if (packet.Length < HeaderLength || packet.Length > MaximumFrameBytes)
        {
            error = "Peer message frame length is invalid.";
            return false;
        }

        if (!packet[..Magic.Length].SequenceEqual(Magic))
        {
            error = "Peer message frame magic is invalid.";
            return false;
        }

        if (packet[4] != Version)
        {
            error = "Peer message frame version is unsupported.";
            return false;
        }

        var frameType = packet[5];
        if (frameType is not (TextFrameType or DeliveryAckFrameType or ReadAckFrameType))
        {
            error = "Peer message frame type is unsupported.";
            return false;
        }

        var offset = 6;
        var encodedMessageId = ReadAscii(packet.Slice(offset, MessageIdLength));
        offset += MessageIdLength;
        if (!TryNormalizeMessageId(encodedMessageId, out var messageId) ||
            !string.Equals(encodedMessageId, messageId, StringComparison.Ordinal))
        {
            error = "Peer message ID is invalid or noncanonical.";
            return false;
        }

        var senderValue = ReadAscii(packet.Slice(offset, PeerIdLength));
        offset += PeerIdLength;
        var recipientValue = ReadAscii(packet.Slice(offset, PeerIdLength));
        offset += PeerIdLength;

        if (!PeerId.TryParse(senderValue, out var senderPeerId) ||
            !PeerId.TryParse(recipientValue, out var recipientPeerId) ||
            senderPeerId == recipientPeerId)
        {
            error = "Peer message sender or recipient is invalid.";
            return false;
        }

        var unixMilliseconds = BinaryPrimitives.ReadInt64BigEndian(packet.Slice(offset, 8));
        offset += 8;
        DateTimeOffset timestamp;
        try
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            error = "Peer message timestamp is invalid.";
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(offset, 4));
        offset += 4;
        if (payloadLength != checked((uint)(packet.Length - HeaderLength)))
        {
            error = "Peer message payload length is invalid.";
            return false;
        }

        if (frameType is DeliveryAckFrameType or ReadAckFrameType)
        {
            if (payloadLength != 0)
            {
                error = "Peer acknowledgement frame must not contain a payload.";
                return false;
            }

            frame = frameType == DeliveryAckFrameType
                ? new PeerDeliveryAckFrame(messageId, senderPeerId, recipientPeerId, timestamp)
                : new PeerReadAckFrame(messageId, senderPeerId, recipientPeerId, timestamp);
            return true;
        }

        if (payloadLength is 0 or > MaximumTextUtf8Bytes)
        {
            error = "Text message payload length is invalid.";
            return false;
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(packet.Slice(offset, checked((int)payloadLength)));
        }
        catch (DecoderFallbackException)
        {
            error = "Text message payload is not valid UTF-8.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextCharacters)
        {
            error = "Text message content is invalid.";
            return false;
        }

        frame = new PeerTextMessageFrame(
            messageId,
            senderPeerId,
            recipientPeerId,
            timestamp,
            text);
        return true;
    }

    public static bool TryValidateForReceiver(
        PeerApplicationFrame frame,
        PeerId localPeerId,
        PeerId expectedRemotePeerId,
        DateTimeOffset now,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(frame);
        error = null;

        if (frame.SenderPeerId != expectedRemotePeerId)
        {
            error = "Peer message sender does not match the authenticated remote peer.";
            return false;
        }

        if (frame.RecipientPeerId != localPeerId)
        {
            error = "Peer message recipient does not match the local identity.";
            return false;
        }

        if (frame.Timestamp > now.AddMinutes(2))
        {
            error = "Peer message timestamp is too far in the future.";
            return false;
        }

        return true;
    }

    public static PeerDeliveryAckFrame CreateDeliveryAck(
        PeerTextMessageFrame message,
        DateTimeOffset deliveredAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new PeerDeliveryAckFrame(
            message.MessageId,
            message.RecipientPeerId,
            message.SenderPeerId,
            deliveredAt);
    }

    public static PeerReadAckFrame CreateReadAck(
        PeerTextMessageFrame message,
        DateTimeOffset readAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new PeerReadAckFrame(
            message.MessageId,
            message.RecipientPeerId,
            message.SenderPeerId,
            readAt);
    }

    private static byte[] EncodeText(PeerTextMessageFrame frame)
    {
        if (string.IsNullOrWhiteSpace(frame.Text) || frame.Text.Length > MaximumTextCharacters)
        {
            throw new ArgumentException(
                $"Text messages must contain 1-{MaximumTextCharacters} characters.",
                nameof(frame));
        }

        var payload = StrictUtf8.GetBytes(frame.Text);
        if (payload.Length > MaximumTextUtf8Bytes)
        {
            throw new ArgumentException("Text message UTF-8 payload is too large.", nameof(frame));
        }

        return EncodeCore(
            TextFrameType,
            frame.MessageId,
            frame.SenderPeerId,
            frame.RecipientPeerId,
            frame.CreatedAt,
            payload);
    }

    private static byte[] EncodeDeliveryAck(PeerDeliveryAckFrame frame)
        => EncodeCore(
            DeliveryAckFrameType,
            frame.MessageId,
            frame.SenderPeerId,
            frame.RecipientPeerId,
            frame.DeliveredAt,
            ReadOnlySpan<byte>.Empty);

    private static byte[] EncodeReadAck(PeerReadAckFrame frame)
        => EncodeCore(
            ReadAckFrameType,
            frame.MessageId,
            frame.SenderPeerId,
            frame.RecipientPeerId,
            frame.ReadAt,
            ReadOnlySpan<byte>.Empty);

    private static byte[] EncodeCore(
        byte frameType,
        string messageId,
        PeerId senderPeerId,
        PeerId recipientPeerId,
        DateTimeOffset timestamp,
        ReadOnlySpan<byte> payload)
    {
        var packet = new byte[HeaderLength + payload.Length];
        var offset = 0;
        Write(Magic, packet, ref offset);
        packet[offset++] = Version;
        packet[offset++] = frameType;
        WriteAscii(messageId, MessageIdLength, packet, ref offset);
        WriteAscii(senderPeerId.Value, PeerIdLength, packet, ref offset);
        WriteAscii(recipientPeerId.Value, PeerIdLength, packet, ref offset);
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(offset, 8), timestamp.ToUnixTimeMilliseconds());
        offset += 8;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(offset, 4), checked((uint)payload.Length));
        offset += 4;
        payload.CopyTo(packet.AsSpan(offset));
        return packet;
    }

    private static void ValidateCommon(
        string messageId,
        PeerId senderPeerId,
        PeerId recipientPeerId,
        DateTimeOffset timestamp)
    {
        if (!TryNormalizeMessageId(messageId, out var normalized) ||
            !string.Equals(messageId, normalized, StringComparison.Ordinal))
        {
            throw new ArgumentException("Message ID must be a lowercase 128-bit hexadecimal identifier.", nameof(messageId));
        }

        if (string.IsNullOrWhiteSpace(senderPeerId.Value) ||
            string.IsNullOrWhiteSpace(recipientPeerId.Value))
        {
            throw new ArgumentException("Sender and recipient Peer IDs must be initialized.");
        }

        if (senderPeerId == recipientPeerId)
        {
            throw new ArgumentException("Sender and recipient Peer IDs must differ.", nameof(recipientPeerId));
        }

        _ = timestamp.ToUnixTimeMilliseconds();
    }

    private static bool TryNormalizeMessageId(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length != MessageIdLength ||
            !value.All(Uri.IsHexDigit))
        {
            return false;
        }

        normalized = value.ToLowerInvariant();
        return true;
    }

    private static string ReadAscii(ReadOnlySpan<byte> value)
    {
        foreach (var item in value)
        {
            if (item > 0x7f)
            {
                return string.Empty;
            }
        }

        return Encoding.ASCII.GetString(value);
    }

    private static void WriteAscii(
        string value,
        int expectedLength,
        Span<byte> destination,
        ref int offset)
    {
        if (value.Length != expectedLength || !value.All(character => character <= 0x7f))
        {
            throw new ArgumentException("Protocol ASCII field has an invalid length or character.", nameof(value));
        }

        var written = Encoding.ASCII.GetBytes(value, destination.Slice(offset, expectedLength));
        if (written != expectedLength)
        {
            throw new InvalidOperationException("Protocol ASCII field encoded to an unexpected length.");
        }

        offset += expectedLength;
    }

    private static void Write(ReadOnlySpan<byte> value, Span<byte> destination, ref int offset)
    {
        value.CopyTo(destination[offset..]);
        offset += value.Length;
    }
}
