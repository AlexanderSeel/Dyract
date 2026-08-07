#if ANDROID
using System.Threading.Channels;
using Dyract.Core.Identity;
using Dyract.Transport;
using Org.Webrtc;

namespace Dyract.Transport.FsWebRtcProbe;

public sealed class FsWebRtcDirectoryHarness : IAsyncDisposable
{
    private readonly IPeerSignalingGateway _gateway;
    private readonly FsWebRtcNegotiationCoordinator _coordinator;
    private readonly PeerId _remotePeerId;
    private readonly string _sessionId;
    private readonly TimeSpan _pollInterval;
    private readonly Channel<ExperimentalOutboundSignal> _outbound = Channel.CreateBounded<ExperimentalOutboundSignal>(
        new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private int _started;
    private int _disposed;

    public FsWebRtcDirectoryHarness(
        IPeerSignalingGateway gateway,
        FsWebRtcNegotiationCoordinator coordinator,
        PeerId remotePeerId,
        string sessionId,
        TimeSpan? pollInterval = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _remotePeerId = remotePeerId;
        _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);

        if (_pollInterval < TimeSpan.FromMilliseconds(250))
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "Diagnostic signaling polling must not run more frequently than every 250 ms.");
        }

        _coordinator.OutboundSignalReady += OnOutboundSignalReady;
        _coordinator.IncomingDataChannel += channel => IncomingDataChannel?.Invoke(channel);
        _coordinator.ConnectionStateChanged += OnConnectionStateChanged;
    }

    public event Action<ExperimentalDataChannelAdapter>? IncomingDataChannel;
    public event Action<Exception>? ProtocolError;

    public Task Connected => _connected.Task;

    public async Task<ExperimentalDataChannelAdapter> StartInitiatorAsync(
        string dataChannelLabel = "dyract",
        CancellationToken cancellationToken = default)
    {
        StartLoops(cancellationToken);
        var channel = _coordinator.CreateOutgoingDataChannel(dataChannelLabel);
        await _coordinator.CreateAndEmitOfferAsync(cancellationToken);
        return channel;
    }

    public Task StartResponderAsync(CancellationToken cancellationToken = default)
    {
        StartLoops(cancellationToken);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _coordinator.OutboundSignalReady -= OnOutboundSignalReady;
        _coordinator.ConnectionStateChanged -= OnConnectionStateChanged;
        _outbound.Writer.TryComplete();
        _runCancellation?.Cancel();

        if (_runTask is not null)
        {
            try
            {
                await _runTask;
            }
            catch (OperationCanceledException) when (_runCancellation?.IsCancellationRequested == true)
            {
            }
        }

        _runCancellation?.Dispose();
        await _coordinator.DisposeAsync();
    }

    private void StartLoops(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("Diagnostic WebRTC directory harness has already been started.");
        }

        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runToken = _runCancellation.Token;
        _runTask = Task.WhenAll(
            SendLoopAsync(runToken),
            ReceiveLoopAsync(runToken));
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var signal in _outbound.Reader.ReadAllAsync(cancellationToken))
        {
            await _gateway.SendAsync(
                _remotePeerId,
                signal.SessionId,
                signal.SignalType,
                signal.Payload,
                cancellationToken);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var envelopes = await _gateway.FetchAsync(cancellationToken);
                foreach (var envelope in envelopes)
                {
                    if (!string.Equals(envelope.SenderPeerId, _remotePeerId.Value, StringComparison.Ordinal) ||
                        !string.Equals(envelope.SessionId, _sessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!PeerNegotiationSignalCodec.TryDecode(
                            envelope,
                            DateTimeOffset.UtcNow,
                            out var signal,
                            out var decodeError) ||
                        signal is null)
                    {
                        ProtocolError?.Invoke(new InvalidOperationException(
                            decodeError ?? "Received negotiation signal could not be decoded."));
                        await _gateway.AcknowledgeAsync([envelope.SignalId], cancellationToken);
                        continue;
                    }

                    await _coordinator.HandleAsync(signal, cancellationToken);
                    await _gateway.AcknowledgeAsync([envelope.SignalId], cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                ProtocolError?.Invoke(exception);
            }

            await Task.Delay(_pollInterval, cancellationToken);
        }
    }

    private void OnOutboundSignalReady(ExperimentalOutboundSignal signal)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (!_outbound.Writer.TryWrite(signal))
        {
            ProtocolError?.Invoke(new InvalidOperationException("Outbound negotiation signal queue is full."));
        }
    }

    private void OnConnectionStateChanged(PeerConnection.PeerConnectionState? state)
    {
        if (state == PeerConnection.PeerConnectionState.Connected)
        {
            _connected.TrySetResult();
            return;
        }

        if (state == PeerConnection.PeerConnectionState.Failed)
        {
            _connected.TrySetException(new InvalidOperationException("Native WebRTC peer connection failed."));
            return;
        }

        if (state == PeerConnection.PeerConnectionState.Closed)
        {
            _connected.TrySetException(new InvalidOperationException("Native WebRTC peer connection closed before becoming connected."));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
#endif
