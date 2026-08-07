#if ANDROID
using Java.Lang;
using Org.Webrtc;

namespace Dyract.Transport.FsWebRtcProbe;

public sealed class PeerConnectionObserverProbe : Object, PeerConnection.IObserver
{
    public void OnSignalingChange(PeerConnection.SignalingState? newState) { }

    public void OnIceConnectionChange(PeerConnection.IceConnectionState? newState) { }

    public void OnIceConnectionReceivingChange(bool receiving) { }

    public void OnIceGatheringChange(PeerConnection.IceGatheringState? newState) { }

    public void OnIceCandidate(IceCandidate? candidate) { }

    public void OnIceCandidatesRemoved(IceCandidate[]? candidates) { }

    public void OnAddStream(MediaStream? stream) { }

    public void OnRemoveStream(MediaStream? stream) { }

    public void OnDataChannel(DataChannel? dataChannel) { }

    public void OnRenegotiationNeeded() { }

    public void OnAddTrack(RtpReceiver? receiver, MediaStream[]? mediaStreams) { }
}

public sealed class SdpObserverProbe : Object, ISdpObserver
{
    public void OnCreateSuccess(SessionDescription? description) { }

    public void OnSetSuccess() { }

    public void OnCreateFailure(string? error) { }

    public void OnSetFailure(string? error) { }
}

public sealed class DataChannelObserverProbe : Object, DataChannel.IObserver
{
    public void OnBufferedAmountChange(long previousAmount) { }

    public void OnStateChange() { }

    public void OnMessage(DataChannel.Buffer? buffer) { }
}
#endif
