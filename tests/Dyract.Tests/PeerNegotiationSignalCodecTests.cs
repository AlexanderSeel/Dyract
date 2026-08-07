using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Dyract.Transport;
using Xunit;

namespace Dyract.Tests;

public sealed class PeerNegotiationSignalCodecTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private const string SignalId = "0123456789abcdef0123456789abcdef";
    private const string SessionId = "abcdef0123456789abcdef0123456789";

    [Fact]
    public void Offer_RoundTripsAsTypedSessionDescription()
    {
        using var sender = PeerIdentity.Generate();
        const string sdp = "v=0\r\ns=- 123 1 IN IP4 127.0.0.1\r\n";
        var envelope = Envelope(
            sender.PeerId.Value,
            PeerSignalTypes.Offer,
            PeerNegotiationSignalCodec.EncodeSessionDescription(sdp));

        var decoded = PeerNegotiationSignalCodec.TryDecode(envelope, Now, out var signal, out var error);

        Assert.True(decoded, error);
        var offer = Assert.IsType<PeerSessionDescriptionSignal>(signal);
        Assert.Equal(PeerSignalTypes.Offer, offer.DescriptionType);
        Assert.Equal(sdp, offer.Sdp);
        Assert.Equal(sender.PeerId, offer.SenderPeerId);
        Assert.Equal(SessionId, offer.SessionId);
    }

    [Fact]
    public void Candidate_RoundTripsCandidateFields()
    {
        using var sender = PeerIdentity.Generate();
        const string candidate = "candidate:1 1 UDP 2122260223 203.0.113.20 52000 typ srflx";
        var envelope = Envelope(
            sender.PeerId.Value,
            PeerSignalTypes.Candidate,
            PeerNegotiationSignalCodec.EncodeIceCandidate("0", 0, candidate));

        var decoded = PeerNegotiationSignalCodec.TryDecode(envelope, Now, out var signal, out var error);

        Assert.True(decoded, error);
        var ice = Assert.IsType<PeerIceCandidateSignal>(signal);
        Assert.Equal("0", ice.SdpMid);
        Assert.Equal(0, ice.SdpMLineIndex);
        Assert.Equal(candidate, ice.Candidate);
    }

    [Fact]
    public void EndOfCandidates_RoundTripsVersionedControlPayload()
    {
        using var sender = PeerIdentity.Generate();
        var envelope = Envelope(
            sender.PeerId.Value,
            PeerSignalTypes.EndOfCandidates,
            PeerNegotiationSignalCodec.EncodeControl());

        var decoded = PeerNegotiationSignalCodec.TryDecode(envelope, Now, out var signal, out var error);

        Assert.True(decoded, error);
        Assert.IsType<PeerEndOfCandidatesSignal>(signal);
    }

    [Fact]
    public void ExpiredEnvelope_IsRejectedBeforePayloadUse()
    {
        using var sender = PeerIdentity.Generate();
        var envelope = new PeerSignalEnvelope(
            SignalId,
            sender.PeerId.Value,
            SessionId,
            PeerSignalTypes.Offer,
            PeerNegotiationSignalCodec.EncodeSessionDescription("v=0"),
            Now.AddSeconds(-50).ToUnixTimeSeconds(),
            Now.AddSeconds(-1).ToUnixTimeSeconds());

        var decoded = PeerNegotiationSignalCodec.TryDecode(envelope, Now, out var signal, out var error);

        Assert.False(decoded);
        Assert.Null(signal);
        Assert.Equal("Signal has expired.", error);
    }

    [Fact]
    public void InvalidSignalId_IsRejectedBeforeAcknowledgementUse()
    {
        using var sender = PeerIdentity.Generate();
        var envelope = new PeerSignalEnvelope(
            "invalid",
            sender.PeerId.Value,
            SessionId,
            PeerSignalTypes.Close,
            PeerNegotiationSignalCodec.EncodeControl(),
            Now.ToUnixTimeSeconds(),
            Now.AddSeconds(45).ToUnixTimeSeconds());

        var decoded = PeerNegotiationSignalCodec.TryDecode(envelope, Now, out var signal, out var error);

        Assert.False(decoded);
        Assert.Null(signal);
        Assert.Equal("Signal ID is invalid.", error);
    }

    [Fact]
    public void InvalidSenderPeerId_IsRejected()
    {
        var envelope = Envelope(
            "not-a-peer-id",
            PeerSignalTypes.Close,
            PeerNegotiationSignalCodec.EncodeControl());

        var decoded = PeerNegotiationSignalCodec.TryDecode(envelope, Now, out var signal, out var error);

        Assert.False(decoded);
        Assert.Null(signal);
        Assert.Equal("Signal sender PeerId is invalid.", error);
    }

    [Fact]
    public void LifetimeLongerThanServerBoundary_IsRejected()
    {
        using var sender = PeerIdentity.Generate();
        var envelope = new PeerSignalEnvelope(
            SignalId,
            sender.PeerId.Value,
            SessionId,
            PeerSignalTypes.Close,
            PeerNegotiationSignalCodec.EncodeControl(),
            Now.ToUnixTimeSeconds(),
            Now.AddSeconds(PeerNegotiationSignalCodec.MaximumSignalLifetimeSeconds + 1).ToUnixTimeSeconds());

        var decoded = PeerNegotiationSignalCodec.TryDecode(envelope, Now, out var signal, out var error);

        Assert.False(decoded);
        Assert.Null(signal);
        Assert.Equal("Signal timestamp ordering or lifetime is invalid.", error);
    }

    [Fact]
    public void UnsupportedPayloadVersion_IsRejected()
    {
        using var sender = PeerIdentity.Generate();
        var envelope = Envelope(
            sender.PeerId.Value,
            PeerSignalTypes.Offer,
            "{\"version\":2,\"sdp\":\"v=0\"}");

        var decoded = PeerNegotiationSignalCodec.TryDecode(envelope, Now, out var signal, out var error);

        Assert.False(decoded);
        Assert.Null(signal);
        Assert.Equal("Session description signal payload is invalid.", error);
    }

    [Fact]
    public void OversizedSessionDescription_IsRejectedBeforeSend()
    {
        var oversized = new string('a', PeerNegotiationSignalCodec.MaximumPayloadBytes + 1);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PeerNegotiationSignalCodec.EncodeSessionDescription(oversized));
    }

    private static PeerSignalEnvelope Envelope(
        string senderPeerId,
        string signalType,
        string payload)
        => new(
            SignalId,
            senderPeerId,
            SessionId,
            signalType,
            payload,
            Now.ToUnixTimeSeconds(),
            Now.AddSeconds(45).ToUnixTimeSeconds());
}
