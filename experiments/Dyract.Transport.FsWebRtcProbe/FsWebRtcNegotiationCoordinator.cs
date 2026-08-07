#if ANDROID
using Dyract.Core.Identity;
using Dyract.Protocol;
using Dyract.Transport;
using Org.Webrtc;

namespace Dyract.Transport.FsWebRtcProbe;

public sealed record ExperimentalOutboundSignal(
    string SessionId,
    string SignalType,
    string Payload);

public sealed class FsWebRtcNegotiationCoordinator : IAsyncDisposable
{
    private readonly FsWebRtcAndroidPeerSession _session;
    private readonly PeerId _remotePeerId;
    private readonly string _sessionId;
    private readonly object _candidateGate = new();
    private readonly Queue<PeerIceCandidateSignal> _pendingRemoteCandidates = new();
    private int _remoteDescriptionApplied;
    private int _endOfCandidatesSent;
    private int _disposed;

    public FsWebRtcNegotiationCoordinator(
        FsWebRtcAndroidPeerSession session,
        PeerId remotePeerId,
        string sessionId)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _remotePeerId = remotePeerId;
        _sessionId = ValidateSessionId(sessionId);

        _session.LocalIceCandidateDiscovered += OnLocalIceCandidateDiscovered;
        _session.IceGatheringStateChanged += OnIceGatheringStateChanged;
        _session.IncomingDataChannel += channel => IncomingDataChannel?.Invoke(channel);
        _session.ConnectionStateChanged += state => ConnectionStateChanged?.Invoke(state);
    }

    public event Action<ExperimentalOutboundSignal>? OutboundSignalReady;
    public event Action<ExperimentalDataChannelAdapter>? IncomingDataChannel;
    public event Action<PeerConnection.PeerConnectionState?>? ConnectionStateChanged;
    public event Action<IceCandidatePrivacySummary>? LocalCandidateSummaryObserved;
    public event Action<IceCandidatePrivacySummary>? RemoteCandidateSummaryObserved;

    public ExperimentalDataChannelAdapter CreateOutgoingDataChannel(string label = "dyract")
    {
        ThrowIfDisposed();
        return _session.CreateOutgoingDataChannel(label);
    }

    public async Task CreateAndEmitOfferAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var offer = await _session.CreateOfferAsync(cancellationToken);
        Emit(PeerSignalTypes.Offer, PeerNegotiationSignalCodec.EncodeSessionDescription(offer.Sdp));
    }

    public Task<SelectedIcePathPrivacySummary?> GetSelectedIcePathSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _session.GetSelectedIcePathSummaryAsync(cancellationToken);
    }

    public async Task HandleAsync(
        PeerNegotiationSignal signal,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(signal);
        EnsureSignalScope(signal);

        switch (signal)
        {
            case PeerSessionDescriptionSignal description:
                await HandleDescriptionAsync(description, cancellationToken);
                return;

            case PeerIceCandidateSignal candidate:
                HandleRemoteCandidate(candidate);
                return;

            case PeerEndOfCandidatesSignal:
                return;

            case PeerCloseSignal:
                await DisposeAsync();
                return;

            default:
                throw new NotSupportedException($"Negotiation signal type {signal.GetType().Name} is not supported.");
        }
    }

    public void EmitClose()
    {
        ThrowIfDisposed();
        Emit(PeerSignalTypes.Close, PeerNegotiationSignalCodec.EncodeControl());
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _session.LocalIceCandidateDiscovered -= OnLocalIceCandidateDiscovered;
        _session.IceGatheringStateChanged -= OnIceGatheringStateChanged;
        await _session.DisposeAsync();
    }

    private async Task HandleDescriptionAsync(
        PeerSessionDescriptionSignal description,
        CancellationToken cancellationToken)
    {
        await _session.ApplyRemoteDescriptionAsync(
            new ExperimentalSessionDescription(description.DescriptionType, description.Sdp),
            cancellationToken);

        Volatile.Write(ref _remoteDescriptionApplied, 1);
        FlushPendingCandidates();

        if (description.DescriptionType == PeerSignalTypes.Offer)
        {
            var answer = await _session.CreateAnswerAsync(cancellationToken);
            Emit(PeerSignalTypes.Answer, PeerNegotiationSignalCodec.EncodeSessionDescription(answer.Sdp));
        }
    }

    private void HandleRemoteCandidate(PeerIceCandidateSignal candidate)
    {
        if (Volatile.Read(ref _remoteDescriptionApplied) == 0)
        {
            lock (_candidateGate)
            {
                if (Volatile.Read(ref _remoteDescriptionApplied) == 0)
                {
                    _pendingRemoteCandidates.Enqueue(candidate);
                    return;
                }
            }
        }

        AddRemoteCandidate(candidate);
    }

    private void FlushPendingCandidates()
    {
        PeerIceCandidateSignal[] candidates;
        lock (_candidateGate)
        {
            candidates = _pendingRemoteCandidates.ToArray();
            _pendingRemoteCandidates.Clear();
        }

        foreach (var candidate in candidates)
        {
            AddRemoteCandidate(candidate);
        }
    }

    private void AddRemoteCandidate(PeerIceCandidateSignal candidate)
    {
        if (IceCandidatePrivacySummary.TryParse(candidate.Candidate, out var summary))
        {
            RemoteCandidateSummaryObserved?.Invoke(summary);
        }

        if (!_session.AddRemoteIceCandidate(new ExperimentalIceCandidate(
                candidate.SdpMid,
                candidate.SdpMLineIndex,
                candidate.Candidate)))
        {
            throw new InvalidOperationException("Native WebRTC rejected the remote ICE candidate.");
        }
    }

    private void OnLocalIceCandidateDiscovered(ExperimentalIceCandidate candidate)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (IceCandidatePrivacySummary.TryParse(candidate.Sdp, out var summary))
        {
            LocalCandidateSummaryObserved?.Invoke(summary);
        }

        Emit(
            PeerSignalTypes.Candidate,
            PeerNegotiationSignalCodec.EncodeIceCandidate(
                candidate.SdpMid,
                candidate.SdpMLineIndex,
                candidate.Sdp));
    }

    private void OnIceGatheringStateChanged(PeerConnection.IceGatheringState? state)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            state != PeerConnection.IceGatheringState.Complete ||
            Interlocked.Exchange(ref _endOfCandidatesSent, 1) != 0)
        {
            return;
        }

        Emit(PeerSignalTypes.EndOfCandidates, PeerNegotiationSignalCodec.EncodeControl());
    }

    private void EnsureSignalScope(PeerNegotiationSignal signal)
    {
        if (signal.SenderPeerId != _remotePeerId)
        {
            throw new InvalidOperationException("Negotiation signal sender does not match the expected remote peer.");
        }

        if (!string.Equals(signal.SessionId, _sessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Negotiation signal belongs to a different session.");
        }
    }

    private void Emit(string signalType, string payload)
        => OutboundSignalReady?.Invoke(new ExperimentalOutboundSignal(_sessionId, signalType, payload));

    private static string ValidateSessionId(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (sessionId.Length != 32 || !sessionId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Session ID must be a 128-bit hexadecimal identifier.", nameof(sessionId));
        }

        return sessionId.ToLowerInvariant();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
#endif
