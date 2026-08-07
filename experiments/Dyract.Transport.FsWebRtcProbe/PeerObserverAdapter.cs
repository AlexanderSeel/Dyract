#if ANDROID
using Org.Webrtc;

namespace Dyract.Transport.FsWebRtcProbe;

public sealed record ExperimentalIceCandidate(
    string? SdpMid,
    int SdpMLineIndex,
    string Sdp);

public sealed class PeerObserverAdapter : Java.Lang.Object, PeerConnection.IObserver
{
    public event Action<ExperimentalIceCandidate>? IceCandidateDiscovered;
    public event Action<DataChannel>? DataChannelReceived;
    public event Action<PeerConnection.IceConnectionState?>? IceConnectionStateChanged;
    public event Action<PeerConnection.PeerConnectionState?>? ConnectionStateChanged;
    public event Action<PeerConnection.IceGatheringState?>? IceGatheringStateChanged;

    public void OnSignalingChange(PeerConnection.SignalingState? newState) { }

    public void OnIceConnectionChange(PeerConnection.IceConnectionState? newState)
    {
        IceConnectionStateChanged?.Invoke(newState);
    }

    public void OnStandardizedIceConnectionChange(PeerConnection.IceConnectionState? newState)
    {
        IceConnectionStateChanged?.Invoke(newState);
    }

    public void OnConnectionChange(PeerConnection.PeerConnectionState? newState)
    {
        ConnectionStateChanged?.Invoke(newState);
    }

    public void OnIceConnectionReceivingChange(bool receiving) { }

    public void OnIceGatheringChange(PeerConnection.IceGatheringState? newState)
    {
        IceGatheringStateChanged?.Invoke(newState);
    }

    public void OnIceCandidate(IceCandidate? candidate)
    {
        if (candidate?.Sdp is not { Length: > 0 } sdp)
        {
            return;
        }

        IceCandidateDiscovered?.Invoke(new ExperimentalIceCandidate(
            candidate.SdpMid,
            candidate.SdpMLineIndex,
            sdp));
    }

    public void OnIceCandidatesRemoved(IceCandidate[]? candidates) { }

    public void OnAddStream(MediaStream? stream) { }

    public void OnRemoveStream(MediaStream? stream) { }

    public void OnDataChannel(DataChannel? dataChannel)
    {
        if (dataChannel is not null)
        {
            DataChannelReceived?.Invoke(dataChannel);
        }
    }

    public void OnRenegotiationNeeded() { }

    public void OnAddTrack(RtpReceiver? receiver, MediaStream[]? mediaStreams) { }
}
#endif
