#if ANDROID
using Org.Webrtc;

namespace Dyract.Transport.FsWebRtcProbe;

public static class PeerConnectionApiProbe
{
    public static PeerConnection? CreatePeerConnection(
        PeerConnectionFactory factory,
        PeerConnection.RTCConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(configuration);

        return factory.CreatePeerConnection(
            configuration,
            (PeerConnection.IObserver)null!);
    }

    public static DataChannel? CreateDataChannel(PeerConnection peerConnection)
    {
        ArgumentNullException.ThrowIfNull(peerConnection);
        return peerConnection.CreateDataChannel("dyract", new DataChannel.Init());
    }

    public static void CreateOffer(PeerConnection peerConnection)
    {
        ArgumentNullException.ThrowIfNull(peerConnection);
        peerConnection.CreateOffer((ISdpObserver)null!, new MediaConstraints());
    }

    public static void CreateAnswer(PeerConnection peerConnection)
    {
        ArgumentNullException.ThrowIfNull(peerConnection);
        peerConnection.CreateAnswer((ISdpObserver)null!, new MediaConstraints());
    }

    public static SessionDescription CreateOfferDescription(string sdp)
        => new(SessionDescription.Type.Offer, sdp);

    public static SessionDescription CreateAnswerDescription(string sdp)
        => new(SessionDescription.Type.Answer, sdp);

    public static void SetLocalDescription(
        PeerConnection peerConnection,
        SessionDescription description)
    {
        ArgumentNullException.ThrowIfNull(peerConnection);
        ArgumentNullException.ThrowIfNull(description);
        peerConnection.SetLocalDescription((ISdpObserver)null!, description);
    }

    public static void SetRemoteDescription(
        PeerConnection peerConnection,
        SessionDescription description)
    {
        ArgumentNullException.ThrowIfNull(peerConnection);
        ArgumentNullException.ThrowIfNull(description);
        peerConnection.SetRemoteDescription((ISdpObserver)null!, description);
    }

    public static bool AddIceCandidate(
        PeerConnection peerConnection,
        string? sdpMid,
        int sdpMLineIndex,
        string candidateSdp)
    {
        ArgumentNullException.ThrowIfNull(peerConnection);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateSdp);
        var candidate = new IceCandidate(sdpMid, sdpMLineIndex, candidateSdp);
        return peerConnection.AddIceCandidate(candidate);
    }
}
#endif
