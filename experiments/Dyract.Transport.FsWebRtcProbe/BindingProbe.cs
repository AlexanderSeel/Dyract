#if ANDROID
using Org.Webrtc;

namespace Dyract.Transport.FsWebRtcProbe;

public static class BindingProbe
{
    public static IReadOnlyList<Type> RequiredTypes { get; } =
    [
        typeof(PeerConnectionFactory),
        typeof(PeerConnection),
        typeof(DataChannel),
        typeof(IceCandidate),
        typeof(SessionDescription)
    ];

    public static bool HasExpectedBindingSurface()
        => RequiredTypes.All(type => type.Assembly is not null);
}
#endif
