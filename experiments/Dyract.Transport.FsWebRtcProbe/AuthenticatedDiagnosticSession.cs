#if ANDROID
using System.Diagnostics;
using System.Security.Cryptography;
using Dyract.Core.Identity;
using Dyract.Crypto.Identity;

namespace Dyract.Transport.FsWebRtcProbe;

public sealed class AuthenticatedDiagnosticSession : IDisposable
{
    private readonly AuthenticatedExperimentalDataChannel _channel;
    private int _disposed;

    private AuthenticatedDiagnosticSession(AuthenticatedExperimentalDataChannel channel)
    {
        _channel = channel;
    }

    public static async Task<AuthenticatedDiagnosticSession> InitiateAsync(
        FsWebRtcDiagnosticConnection connection,
        PeerIdentity localIdentity,
        PeerId remotePeerId,
        ReadOnlyMemory<byte> remoteIdentityPublicKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var rawChannel = await connection.GetDataChannelAsync(cancellationToken);
        var authenticated = await AuthenticatedExperimentalDataChannel.InitiateAsync(
            rawChannel,
            localIdentity,
            remotePeerId,
            remoteIdentityPublicKey,
            connection.SessionId,
            cancellationToken);
        return new AuthenticatedDiagnosticSession(authenticated);
    }

    public static async Task<AuthenticatedDiagnosticSession> RespondAsync(
        FsWebRtcDiagnosticConnection connection,
        PeerIdentity localIdentity,
        PeerId remotePeerId,
        ReadOnlyMemory<byte> remoteIdentityPublicKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var rawChannel = await connection.GetDataChannelAsync(cancellationToken);
        var authenticated = await AuthenticatedExperimentalDataChannel.RespondAsync(
            rawChannel,
            localIdentity,
            remotePeerId,
            remoteIdentityPublicKey,
            connection.SessionId,
            cancellationToken);
        return new AuthenticatedDiagnosticSession(authenticated);
    }

    public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var token = RandomNumberGenerator.GetBytes(DiagnosticFrame.TokenLength);
        var ping = DiagnosticFrame.Create(DiagnosticFrame.PingType, token);
        var stopwatch = Stopwatch.StartNew();

        await _channel.SendAsync(ping, cancellationToken);

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

    public async Task RunEchoResponderAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await foreach (var frame in _channel.ReceiveAsync(cancellationToken))
        {
            try
            {
                if (!DiagnosticFrame.TryParse(frame, out var type, out var token) ||
                    type != DiagnosticFrame.PingType)
                {
                    continue;
                }

                await _channel.SendAsync(
                    DiagnosticFrame.Create(DiagnosticFrame.PongType, token),
                    cancellationToken);
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
#endif
