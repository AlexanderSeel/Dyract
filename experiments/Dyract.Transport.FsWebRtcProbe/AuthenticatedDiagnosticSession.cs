#if ANDROID
using System.Diagnostics;
using System.Security.Cryptography;
using Dyract.Core.Identity;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Dyract.Transport;

namespace Dyract.Transport.FsWebRtcProbe;

public sealed record AuthenticatedMessageAckProbeResult(
    string MessageId,
    TimeSpan RoundTripTime);

public sealed class AuthenticatedDiagnosticSession : IDisposable, IPeerApplicationFrameSender
{
    private static readonly byte[] MessagingMagic = "DYRM"u8.ToArray();
    private readonly AuthenticatedExperimentalDataChannel _channel;
    private readonly PeerId _localPeerId;
    private readonly PeerId _remotePeerId;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private int _disposed;

    private AuthenticatedDiagnosticSession(
        AuthenticatedExperimentalDataChannel channel,
        PeerId localPeerId,
        PeerId remotePeerId)
    {
        _channel = channel;
        _localPeerId = localPeerId;
        _remotePeerId = remotePeerId;
    }

    public PeerId LocalPeerId => _localPeerId;
    public PeerId RemotePeerId => _remotePeerId;

    public static async Task<AuthenticatedDiagnosticSession> InitiateAsync(
        FsWebRtcDiagnosticConnection connection,
        PeerIdentity localIdentity,
        PeerId remotePeerId,
        ReadOnlyMemory<byte> remoteIdentityPublicKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(localIdentity);
        var rawChannel = await connection.GetDataChannelAsync(cancellationToken);
        var authenticated = await AuthenticatedExperimentalDataChannel.InitiateAsync(
            rawChannel,
            localIdentity,
            remotePeerId,
            remoteIdentityPublicKey,
            connection.SessionId,
            cancellationToken);
        return new AuthenticatedDiagnosticSession(
            authenticated,
            localIdentity.PeerId,
            remotePeerId);
    }

    public static async Task<AuthenticatedDiagnosticSession> RespondAsync(
        FsWebRtcDiagnosticConnection connection,
        PeerIdentity localIdentity,
        PeerId remotePeerId,
        ReadOnlyMemory<byte> remoteIdentityPublicKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(localIdentity);
        var rawChannel = await connection.GetDataChannelAsync(cancellationToken);
        var authenticated = await AuthenticatedExperimentalDataChannel.RespondAsync(
            rawChannel,
            localIdentity,
            remotePeerId,
            remoteIdentityPublicKey,
            connection.SessionId,
            cancellationToken);
        return new AuthenticatedDiagnosticSession(
            authenticated,
            localIdentity.PeerId,
            remotePeerId);
    }

    public async Task SendAsync(
        PeerId recipientPeerId,
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (recipientPeerId != _remotePeerId)
        {
            throw new InvalidOperationException(
                "Authenticated diagnostic session cannot send an application frame to a different peer.");
        }

        await _channel.SendAsync(frame, cancellationToken);
    }

    public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _probeGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            return await PingCoreAsync(cancellationToken);
        }
        finally
        {
            _probeGate.Release();
        }
    }

    public async Task<AuthenticatedMessageAckProbeResult> MessageAckProbeAsync(
        string text = "Dyract authenticated message ACK probe",
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _probeGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            return await MessageAckProbeCoreAsync(text, cancellationToken);
        }
        finally
        {
            _probeGate.Release();
        }
    }

    public async Task RunEchoResponderAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await foreach (var frame in _channel.ReceiveAsync(cancellationToken))
        {
            try
            {
                if (DiagnosticFrame.TryParse(frame, out var type, out var token) &&
                    type == DiagnosticFrame.PingType)
                {
                    var pong = DiagnosticFrame.Create(DiagnosticFrame.PongType, token);
                    try
                    {
                        await _channel.SendAsync(pong, cancellationToken);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(pong);
                    }

                    continue;
                }

                if (!LooksLikeMessagingFrame(frame))
                {
                    continue;
                }

                if (!PeerMessagingProtocol.TryDecode(frame, out var decoded, out var decodeError) ||
                    decoded is null)
                {
                    throw new InvalidDataException(
                        decodeError ?? "Authenticated diagnostic DYRM request could not be decoded.");
                }

                if (!PeerMessagingProtocol.TryValidateForReceiver(
                        decoded,
                        _localPeerId,
                        _remotePeerId,
                        DateTimeOffset.UtcNow,
                        out var validationError))
                {
                    throw new InvalidDataException(
                        validationError ?? "Authenticated diagnostic DYRM request failed peer-scope validation.");
                }

                if (decoded is PeerTextMessageFrame textMessage)
                {
                    var deliveredAt = DateTimeOffset.FromUnixTimeMilliseconds(
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    var ackPacket = PeerMessagingProtocol.Encode(
                        PeerMessagingProtocol.CreateDeliveryAck(textMessage, deliveredAt));
                    try
                    {
                        await _channel.SendAsync(ackPacket, cancellationToken);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(ackPacket);
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(frame);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Dispose();
    }

    private async Task<TimeSpan> PingCoreAsync(CancellationToken cancellationToken)
    {
        var token = RandomNumberGenerator.GetBytes(DiagnosticFrame.TokenLength);
        var ping = DiagnosticFrame.Create(DiagnosticFrame.PingType, token);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _channel.SendAsync(ping, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ping);
        }

        await foreach (var frame in _channel.ReceiveAsync(cancellationToken))
        {
            try
            {
                if (!DiagnosticFrame.TryParse(frame, out var type, out var receivedToken) ||
                    type != DiagnosticFrame.PongType ||
                    !receivedToken.AsSpan().SequenceEqual(token))
                {
                    continue;
                }

                stopwatch.Stop();
                return stopwatch.Elapsed;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(frame);
            }
        }

        throw new EndOfStreamException("Authenticated diagnostic channel closed before the matching pong was received.");
    }

    private async Task<AuthenticatedMessageAckProbeResult> MessageAckProbeCoreAsync(
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(text), "Diagnostic message probe is limited to 512 characters.");
        }

        var messageId = Guid.CreateVersion7().ToString("N");
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var packet = PeerMessagingProtocol.Encode(new PeerTextMessageFrame(
            messageId,
            _localPeerId,
            _remotePeerId,
            createdAt,
            text));
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _channel.SendAsync(packet, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(packet);
        }

        await foreach (var frame in _channel.ReceiveAsync(cancellationToken))
        {
            try
            {
                if (!LooksLikeMessagingFrame(frame))
                {
                    continue;
                }

                if (!PeerMessagingProtocol.TryDecode(frame, out var decoded, out var decodeError) ||
                    decoded is null)
                {
                    throw new InvalidDataException(
                        decodeError ?? "Authenticated diagnostic DYRM response could not be decoded.");
                }

                if (!PeerMessagingProtocol.TryValidateForReceiver(
                        decoded,
                        _localPeerId,
                        _remotePeerId,
                        DateTimeOffset.UtcNow,
                        out var validationError))
                {
                    throw new InvalidDataException(
                        validationError ?? "Authenticated diagnostic DYRM response failed peer-scope validation.");
                }

                if (decoded is not PeerDeliveryAckFrame ack ||
                    !string.Equals(ack.MessageId, messageId, StringComparison.Ordinal))
                {
                    continue;
                }

                stopwatch.Stop();
                return new AuthenticatedMessageAckProbeResult(
                    messageId,
                    stopwatch.Elapsed);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(frame);
            }
        }

        throw new EndOfStreamException(
            "Authenticated diagnostic channel closed before the matching DYRM delivery ACK was received.");
    }

    private static bool LooksLikeMessagingFrame(ReadOnlySpan<byte> frame)
        => frame.Length >= MessagingMagic.Length &&
           frame[..MessagingMagic.Length].SequenceEqual(MessagingMagic);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
#endif
