#if ANDROID
using Android.Content;
using Org.Webrtc;

namespace Dyract.Transport.FsWebRtcProbe;

public static class FactoryProbe
{
    public static PeerConnectionFactory CreateFactory(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var initializationOptions = PeerConnectionFactory.InitializationOptions
            .InvokeBuilder(context.ApplicationContext ?? context)
            .CreateInitializationOptions();

        PeerConnectionFactory.Initialize(initializationOptions);

        return PeerConnectionFactory
            .InvokeBuilder()
            .CreatePeerConnectionFactory();
    }

    public static PeerConnection.RTCConfiguration CreateDirectConfiguration(
        params string[] stunUris)
    {
        ArgumentNullException.ThrowIfNull(stunUris);

        var iceServers = stunUris
            .Where(uri => !string.IsNullOrWhiteSpace(uri))
            .Select(uri => PeerConnection.IceServer.InvokeBuilder(uri).CreateIceServer())
            .ToList();

        return new PeerConnection.RTCConfiguration(iceServers);
    }
}
#endif
