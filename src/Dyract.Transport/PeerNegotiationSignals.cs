using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dyract.Core.Identity;
using Dyract.Protocol;

namespace Dyract.Transport;

public abstract record PeerNegotiationSignal(
    string SignalId,
    PeerId SenderPeerId,
    string SessionId);

public sealed record PeerSessionDescriptionSignal(
    string SignalId,
    PeerId SenderPeerId,
    string SessionId,
    string DescriptionType,
    string Sdp)
    : PeerNegotiationSignal(SignalId, SenderPeerId, SessionId);

public sealed record PeerIceCandidateSignal(
    string SignalId,
    PeerId SenderPeerId,
    string SessionId,
    string? SdpMid,
    int SdpMLineIndex,
    string Candidate)
    : PeerNegotiationSignal(SignalId, SenderPeerId, SessionId);

public sealed record PeerEndOfCandidatesSignal(
    string SignalId,
    PeerId SenderPeerId,
    string SessionId)
    : PeerNegotiationSignal(SignalId, SenderPeerId, SessionId);

public sealed record PeerCloseSignal(
    string SignalId,
    PeerId SenderPeerId,
    string SessionId)
    : PeerNegotiationSignal(SignalId, SenderPeerId, SessionId);

public static class PeerNegotiationSignalCodec
{
    public const int CurrentVersion = 1;
    public const int MaximumPayloadBytes = 32 * 1024;
    public const int MaximumSignalLifetimeSeconds = 60;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string EncodeSessionDescription(string sdp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sdp);
        return SerializeChecked(new SessionDescriptionPayload(CurrentVersion, sdp));
    }

    public static string EncodeIceCandidate(
        string? sdpMid,
        int sdpMLineIndex,
        string candidate)
    {
        if (sdpMLineIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sdpMLineIndex));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        return SerializeChecked(new IceCandidatePayload(
            CurrentVersion,
            sdpMid,
            sdpMLineIndex,
            candidate));
    }

    public static string EncodeControl()
        => JsonSerializer.Serialize(new ControlPayload(CurrentVersion), JsonOptions);

    public static bool TryDecode(
        PeerSignalEnvelope envelope,
        DateTimeOffset now,
        out PeerNegotiationSignal? signal,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        signal = null;
        error = null;

        if (!IsHexId(envelope.SignalId))
        {
            error = "Signal ID is invalid.";
            return false;
        }

        if (!PeerId.TryParse(envelope.SenderPeerId, out var senderPeerId))
        {
            error = "Signal sender PeerId is invalid.";
            return false;
        }

        if (!IsHexId(envelope.SessionId))
        {
            error = "Signal session ID is invalid.";
            return false;
        }

        if (!TryUnixTime(envelope.CreatedUnixSeconds, out var createdAt) ||
            !TryUnixTime(envelope.ExpiresUnixSeconds, out var expiresAt))
        {
            error = "Signal timestamps are invalid.";
            return false;
        }

        if (createdAt > expiresAt ||
            expiresAt - createdAt > TimeSpan.FromSeconds(MaximumSignalLifetimeSeconds) ||
            createdAt > now.AddMinutes(2))
        {
            error = "Signal timestamp ordering or lifetime is invalid.";
            return false;
        }

        if (expiresAt <= now)
        {
            error = "Signal has expired.";
            return false;
        }

        if (envelope.Payload is null || Encoding.UTF8.GetByteCount(envelope.Payload) > MaximumPayloadBytes)
        {
            error = $"Signal payload exceeds {MaximumPayloadBytes} UTF-8 bytes.";
            return false;
        }

        try
        {
            switch (envelope.SignalType)
            {
                case PeerSignalTypes.Offer:
                case PeerSignalTypes.Answer:
                {
                    var payload = JsonSerializer.Deserialize<SessionDescriptionPayload>(envelope.Payload, JsonOptions);
                    if (payload is null || payload.Version != CurrentVersion || string.IsNullOrWhiteSpace(payload.Sdp))
                    {
                        error = "Session description signal payload is invalid.";
                        return false;
                    }

                    signal = new PeerSessionDescriptionSignal(
                        envelope.SignalId,
                        senderPeerId,
                        envelope.SessionId,
                        envelope.SignalType,
                        payload.Sdp);
                    return true;
                }

                case PeerSignalTypes.Candidate:
                {
                    var payload = JsonSerializer.Deserialize<IceCandidatePayload>(envelope.Payload, JsonOptions);
                    if (payload is null ||
                        payload.Version != CurrentVersion ||
                        payload.SdpMLineIndex < 0 ||
                        string.IsNullOrWhiteSpace(payload.Candidate))
                    {
                        error = "ICE candidate signal payload is invalid.";
                        return false;
                    }

                    signal = new PeerIceCandidateSignal(
                        envelope.SignalId,
                        senderPeerId,
                        envelope.SessionId,
                        payload.SdpMid,
                        payload.SdpMLineIndex,
                        payload.Candidate);
                    return true;
                }

                case PeerSignalTypes.EndOfCandidates:
                    if (!TryDecodeControl(envelope.Payload))
                    {
                        error = "End-of-candidates signal payload is invalid.";
                        return false;
                    }

                    signal = new PeerEndOfCandidatesSignal(
                        envelope.SignalId,
                        senderPeerId,
                        envelope.SessionId);
                    return true;

                case PeerSignalTypes.Close:
                    if (!TryDecodeControl(envelope.Payload))
                    {
                        error = "Close signal payload is invalid.";
                        return false;
                    }

                    signal = new PeerCloseSignal(
                        envelope.SignalId,
                        senderPeerId,
                        envelope.SessionId);
                    return true;

                default:
                    error = "Signal type is not supported by the transport negotiation codec.";
                    return false;
            }
        }
        catch (JsonException)
        {
            error = "Signal payload is not valid JSON.";
            return false;
        }
    }

    private static bool TryDecodeControl(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        var decoded = JsonSerializer.Deserialize<ControlPayload>(payload, JsonOptions);
        return decoded?.Version == CurrentVersion;
    }

    private static string SerializeChecked<T>(T payload)
    {
        var encoded = JsonSerializer.Serialize(payload, JsonOptions);
        if (Encoding.UTF8.GetByteCount(encoded) > MaximumPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), $"Encoded signal payload exceeds {MaximumPayloadBytes} UTF-8 bytes.");
        }

        return encoded;
    }

    private static bool TryUnixTime(long unixSeconds, out DateTimeOffset value)
    {
        try
        {
            value = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            value = default;
            return false;
        }
    }

    private static bool IsHexId(string? value)
        => value is { Length: 32 } && value.All(Uri.IsHexDigit);

    private sealed record SessionDescriptionPayload(int Version, string Sdp);
    private sealed record IceCandidatePayload(int Version, string? SdpMid, int SdpMLineIndex, string Candidate);
    private sealed record ControlPayload(int Version);
}
