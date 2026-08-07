#if ANDROID
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using Android.Content;
using Dyract.Core.Identity;
using Dyract.Protocol;
using Dyract.Transport;

namespace Dyract.Transport.FsWebRtcProbe;

public sealed class FsWebRtcDiagnosticController
{
    private readonly Context _context;
    private readonly IPeerSignalingGateway _gateway;
    private readonly string[] _stunUris;

    public FsWebRtcDiagnosticController(
        Context context,
        IPeerSignalingGateway gateway,
        params string[] stunUris)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _stunUris = stunUris ?? throw new ArgumentNullException(nameof(stunUris));
    }

    public async Task<FsWebRtcDiagnosticConnection> StartInitiatorAsync(
        PeerId remotePeerId,
        CancellationToken cancellationToken = default)
    {
        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var components = CreateComponents(remotePeerId, sessionId);

        try
        {
            var dataChannel = await components.Harness.StartInitiatorAsync(
                cancellationToken: cancellationToken);

            return new FsWebRtcDiagnosticConnection(
                remotePeerId,
                sessionId,
                components.Harness,
                dataChannel,
                incomingDataChannel: null);
        }
        catch
        {
            await components.Harness.DisposeAsync();
            throw;
        }
    }

    public async Task<FsWebRtcDiagnosticConnection> WaitForOfferAndStartResponderAsync(
        PeerId remotePeerId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var token = timeoutSource.Token;

        while (true)
        {
            token.ThrowIfCancellationRequested();
            var envelopes = await _gateway.FetchAsync(token);

            foreach (var envelope in envelopes)
            {
                if (!string.Equals(envelope.SenderPeerId, remotePeerId.Value, StringComparison.Ordinal) ||
                    envelope.SignalType != PeerSignalTypes.Offer)
                {
                    continue;
                }

                if (!PeerNegotiationSignalCodec.TryDecode(
                        envelope,
                        DateTimeOffset.UtcNow,
                        out var decoded,
                        out var decodeError) ||
                    decoded is not PeerSessionDescriptionSignal offer)
                {
                    await _gateway.AcknowledgeAsync([envelope.SignalId], token);
                    throw new InvalidOperationException(
                        decodeError ?? "Matching diagnostic offer could not be decoded.");
                }

                var components = CreateComponents(remotePeerId, offer.SessionId);
                var incomingChannelSource = new TaskCompletionSource<ExperimentalDataChannelAdapter>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                components.Harness.IncomingDataChannel += channel => incomingChannelSource.TrySetResult(channel);

                try
                {
                    // The harness subscribes to coordinator output at construction time, so the
                    // answer and any immediate local candidates queue safely before its loops start.
                    await components.Coordinator.HandleAsync(offer, token);
                    await _gateway.AcknowledgeAsync([envelope.SignalId], token);
                    await components.Harness.StartResponderAsync(token);

                    return new FsWebRtcDiagnosticConnection(
                        remotePeerId,
                        offer.SessionId,
                        components.Harness,
                        outgoingDataChannel: null,
                        incomingDataChannel: incomingChannelSource.Task);
                }
                catch
                {
                    await components.Harness.DisposeAsync();
                    throw;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
    }

    private Components CreateComponents(PeerId remotePeerId, string sessionId)
    {
        var peerSession = new FsWebRtcAndroidPeerSession(_context, _stunUris);
        var coordinator = new FsWebRtcNegotiationCoordinator(peerSession, remotePeerId, sessionId);
        var harness = new FsWebRtcDirectoryHarness(_gateway, coordinator, remotePeerId, sessionId);
        return new Components(coordinator, harness);
    }

    private sealed record Components(
        FsWebRtcNegotiationCoordinator Coordinator,
        FsWebRtcDirectoryHarness Harness);
}

public sealed class FsWebRtcDiagnosticConnection : IAsyncDisposable
{
    private readonly FsWebRtcDirectoryHarness _harness;
    private readonly ExperimentalDataChannelAdapter? _outgoingDataChannel;
    private readonly Task<ExperimentalDataChannelAdapter>? _incomingDataChannel;
    private readonly object _iceSummaryGate = new();
    private readonly HashSet<string> _localCandidateSummaries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _remoteCandidateSummaries = new(StringComparer.Ordinal);
    private int _disposed;

    internal FsWebRtcDiagnosticConnection(
        PeerId remotePeerId,
        string sessionId,
        FsWebRtcDirectoryHarness harness,
        ExperimentalDataChannelAdapter? outgoingDataChannel,
        Task<ExperimentalDataChannelAdapter>? incomingDataChannel)
    {
        RemotePeerId = remotePeerId;
        SessionId = sessionId;
        _harness = harness;
        _outgoingDataChannel = outgoingDataChannel;
        _incomingDataChannel = incomingDataChannel;
        _harness.LocalCandidateSummaryObserved += OnLocalCandidateSummaryObserved;
        _harness.RemoteCandidateSummaryObserved += OnRemoteCandidateSummaryObserved;
    }

    public PeerId RemotePeerId { get; }
    public string SessionId { get; }
    public Task Connected => _harness.Connected;
    public event Action? IceCandidateSummaryChanged;

    public string LocalIceCandidateSummary
    {
        get
        {
            lock (_iceSummaryGate)
            {
                return FormatCandidateSummaries(_localCandidateSummaries);
            }
        }
    }

    public string RemoteIceCandidateSummary
    {
        get
        {
            lock (_iceSummaryGate)
            {
                return FormatCandidateSummaries(_remoteCandidateSummaries);
            }
        }
    }

    public async Task<ExperimentalDataChannelAdapter> GetDataChannelAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_outgoingDataChannel is not null)
        {
            return _outgoingDataChannel;
        }

        if (_incomingDataChannel is null)
        {
            throw new InvalidOperationException("Diagnostic connection has no DataChannel source.");
        }

        return await _incomingDataChannel.WaitAsync(cancellationToken);
    }

    public async Task WaitForDataChannelOpenAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var channel = await GetDataChannelAsync(cancellationToken);
        await channel.Opened.WaitAsync(cancellationToken);
        ThrowIfDisposed();
    }

    public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var channel = await GetDataChannelAsync(cancellationToken);
        await channel.Opened.WaitAsync(cancellationToken);

        var token = RandomNumberGenerator.GetBytes(DiagnosticFrame.TokenLength);
        var ping = DiagnosticFrame.Create(DiagnosticFrame.PingType, token);
        var stopwatch = Stopwatch.StartNew();

        await channel.SendAsync(ping, cancellationToken);

        await foreach (var frame in channel.ReceiveAsync(cancellationToken))
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

        throw new EndOfStreamException("Diagnostic DataChannel closed before the matching pong was received.");
    }

    public async Task RunEchoResponderAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var channel = await GetDataChannelAsync(cancellationToken);
        await channel.Opened.WaitAsync(cancellationToken);

        await foreach (var frame in channel.ReceiveAsync(cancellationToken))
        {
            if (!DiagnosticFrame.TryParse(frame, out var type, out var token) ||
                type != DiagnosticFrame.PingType)
            {
                continue;
            }

            await channel.SendAsync(
                DiagnosticFrame.Create(DiagnosticFrame.PongType, token),
                cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _harness.LocalCandidateSummaryObserved -= OnLocalCandidateSummaryObserved;
        _harness.RemoteCandidateSummaryObserved -= OnRemoteCandidateSummaryObserved;
        return _harness.DisposeAsync();
    }

    private void OnLocalCandidateSummaryObserved(IceCandidatePrivacySummary summary)
        => AddCandidateSummary(_localCandidateSummaries, summary.DisplayValue);

    private void OnRemoteCandidateSummaryObserved(IceCandidatePrivacySummary summary)
        => AddCandidateSummary(_remoteCandidateSummaries, summary.DisplayValue);

    private void AddCandidateSummary(HashSet<string> target, string value)
    {
        var changed = false;
        lock (_iceSummaryGate)
        {
            changed = target.Add(value);
        }

        if (changed && Volatile.Read(ref _disposed) == 0)
        {
            IceCandidateSummaryChanged?.Invoke();
        }
    }

    private static string FormatCandidateSummaries(HashSet<string> summaries)
        => summaries.Count == 0
            ? "none observed"
            : string.Join(", ", summaries.OrderBy(value => value, StringComparer.Ordinal));

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}

internal static class DiagnosticFrame
{
    private static readonly byte[] Magic = "DYRT"u8.ToArray();
    public const byte Version = 1;
    public const byte PingType = 1;
    public const byte PongType = 2;
    public const int TokenLength = 16;
    private const int HeaderLength = 6;
    private const int TimestampLength = 8;
    private const int FrameLength = HeaderLength + TokenLength + TimestampLength;

    public static byte[] Create(byte type, ReadOnlySpan<byte> token)
    {
        if (type is not (PingType or PongType))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        if (token.Length != TokenLength)
        {
            throw new ArgumentException($"Diagnostic token must contain exactly {TokenLength} bytes.", nameof(token));
        }

        var frame = new byte[FrameLength];
        Magic.CopyTo(frame, 0);
        frame[4] = Version;
        frame[5] = type;
        token.CopyTo(frame.AsSpan(HeaderLength, TokenLength));
        BinaryPrimitives.WriteInt64BigEndian(
            frame.AsSpan(HeaderLength + TokenLength, TimestampLength),
            Stopwatch.GetTimestamp());
        return frame;
    }

    public static bool TryParse(
        ReadOnlySpan<byte> frame,
        out byte type,
        out byte[] token)
    {
        type = 0;
        token = [];

        if (frame.Length != FrameLength ||
            !frame[..Magic.Length].SequenceEqual(Magic) ||
            frame[4] != Version ||
            frame[5] is not (PingType or PongType))
        {
            return false;
        }

        type = frame[5];
        token = frame.Slice(HeaderLength, TokenLength).ToArray();
        return true;
    }
}
#endif