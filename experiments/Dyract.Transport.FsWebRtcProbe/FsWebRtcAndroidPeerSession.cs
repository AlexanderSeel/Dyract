#if ANDROID
using Android.Content;
using Org.Webrtc;

namespace Dyract.Transport.FsWebRtcProbe;

public sealed record ExperimentalSessionDescription(string Type, string Sdp);

public sealed class FsWebRtcAndroidPeerSession : IAsyncDisposable
{
    private readonly PeerConnectionFactory _factory;
    private readonly PeerConnection _peerConnection;
    private readonly PeerObserverAdapter _observer;
    private readonly object _channelGate = new();
    private readonly List<ExperimentalDataChannelAdapter> _channels = [];
    private int _disposed;

    public FsWebRtcAndroidPeerSession(Context context, params string[] stunUris)
    {
        ArgumentNullException.ThrowIfNull(context);

        _factory = FactoryProbe.CreateFactory(context);
        var configuration = FactoryProbe.CreateDirectConfiguration(stunUris);
        _observer = new PeerObserverAdapter();
        _observer.IceCandidateDiscovered += candidate => LocalIceCandidateDiscovered?.Invoke(candidate);
        _observer.DataChannelReceived += dataChannel =>
        {
            var adapter = TrackChannel(new ExperimentalDataChannelAdapter(dataChannel));
            IncomingDataChannel?.Invoke(adapter);
        };
        _observer.IceConnectionStateChanged += state => IceConnectionStateChanged?.Invoke(state);
        _observer.ConnectionStateChanged += state => ConnectionStateChanged?.Invoke(state);
        _observer.IceGatheringStateChanged += state => IceGatheringStateChanged?.Invoke(state);

        _peerConnection = _factory.CreatePeerConnection(configuration, _observer)
            ?? throw new InvalidOperationException("Native WebRTC failed to create a peer connection.");
    }

    public event Action<ExperimentalIceCandidate>? LocalIceCandidateDiscovered;
    public event Action<ExperimentalDataChannelAdapter>? IncomingDataChannel;
    public event Action<PeerConnection.IceConnectionState?>? IceConnectionStateChanged;
    public event Action<PeerConnection.PeerConnectionState?>? ConnectionStateChanged;
    public event Action<PeerConnection.IceGatheringState?>? IceGatheringStateChanged;

    public ExperimentalDataChannelAdapter CreateOutgoingDataChannel(string label = "dyract")
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var nativeChannel = _peerConnection.CreateDataChannel(label, new DataChannel.Init())
            ?? throw new InvalidOperationException("Native WebRTC failed to create a DataChannel.");

        return TrackChannel(new ExperimentalDataChannelAdapter(nativeChannel));
    }

    public async Task<ExperimentalSessionDescription> CreateOfferAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var observer = new CreateSdpObserver();
        using var constraints = new MediaConstraints();
        _peerConnection.CreateOffer(observer, constraints);

        var description = await observer.Task.WaitAsync(cancellationToken);
        await SetLocalDescriptionAsync(description, cancellationToken);
        return new ExperimentalSessionDescription("offer", RequireSdp(description));
    }

    public async Task<ExperimentalSessionDescription> CreateAnswerAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var observer = new CreateSdpObserver();
        using var constraints = new MediaConstraints();
        _peerConnection.CreateAnswer(observer, constraints);

        var description = await observer.Task.WaitAsync(cancellationToken);
        await SetLocalDescriptionAsync(description, cancellationToken);
        return new ExperimentalSessionDescription("answer", RequireSdp(description));
    }

    public async Task ApplyRemoteDescriptionAsync(
        ExperimentalSessionDescription description,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(description.Sdp);

        var type = description.Type switch
        {
            "offer" => SessionDescription.Type.Offer,
            "answer" => SessionDescription.Type.Answer,
            _ => throw new ArgumentException("Remote description type must be offer or answer.", nameof(description))
        };

        using var nativeDescription = new SessionDescription(type, description.Sdp);
        using var observer = new SetSdpObserver();
        _peerConnection.SetRemoteDescription(observer, nativeDescription);
        await observer.Task.WaitAsync(cancellationToken);
    }

    public bool AddRemoteIceCandidate(ExperimentalIceCandidate candidate)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Sdp);

        using var nativeCandidate = new IceCandidate(
            candidate.SdpMid,
            candidate.SdpMLineIndex,
            candidate.Sdp);
        return _peerConnection.AddIceCandidate(nativeCandidate);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        ExperimentalDataChannelAdapter[] channels;
        lock (_channelGate)
        {
            channels = _channels.ToArray();
            _channels.Clear();
        }

        return DisposeCoreAsync(channels);
    }

    private async ValueTask DisposeCoreAsync(ExperimentalDataChannelAdapter[] channels)
    {
        foreach (var channel in channels)
        {
            await channel.DisposeAsync();
        }

        _peerConnection.Close();
        _observer.Dispose();
        _factory.Dispose();
    }

    private async Task SetLocalDescriptionAsync(
        SessionDescription description,
        CancellationToken cancellationToken)
    {
        using var observer = new SetSdpObserver();
        _peerConnection.SetLocalDescription(observer, description);
        await observer.Task.WaitAsync(cancellationToken);
    }

    private ExperimentalDataChannelAdapter TrackChannel(ExperimentalDataChannelAdapter channel)
    {
        lock (_channelGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                channel.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw new ObjectDisposedException(nameof(FsWebRtcAndroidPeerSession));
            }

            _channels.Add(channel);
            return channel;
        }
    }

    private static string RequireSdp(SessionDescription description)
        => description.Description
            ?? throw new InvalidOperationException("Native WebRTC returned an SDP description without text.");

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
#endif
