#if ANDROID
using Android.Content;
using Org.Webrtc;

namespace Dyract.Transport.FsWebRtcProbe;

public static class FactoryProbe
{
    public static PeerConnectionFactory CreateFactory(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var initializationBuilder = PeerConnectionFactory.InitializationOptions
            .InvokeBuilder(context.ApplicationContext ?? context)
            ?? throw new InvalidOperationException("Native WebRTC failed to create initialization options builder.");
        var initializationOptions = initializationBuilder.CreateInitializationOptions()
            ?? throw new InvalidOperationException("Native WebRTC failed to create initialization options.");

        PeerConnectionFactory.Initialize(initializationOptions);

        var factoryBuilder = PeerConnectionFactory.InvokeBuilder()
            ?? throw new InvalidOperationException("Native WebRTC failed to create peer connection factory builder.");
        return factoryBuilder.CreatePeerConnectionFactory()
            ?? throw new InvalidOperationException("Native WebRTC failed to create peer connection factory.");
    }

    public static PeerConnection.RTCConfiguration CreateDirectConfiguration(
        params string[] stunUris)
    {
        ArgumentNullException.ThrowIfNull(stunUris);

        var iceServers = new List<PeerConnection.IceServer>();
        foreach (var uri in stunUris.Where(uri => !string.IsNullOrWhiteSpace(uri)))
        {
            var builder = PeerConnection.IceServer.InvokeBuilder(uri)
                ?? throw new InvalidOperationException("Native WebRTC failed to create ICE server builder.");
            var server = builder.CreateIceServer()
                ?? throw new InvalidOperationException("Native WebRTC failed to create ICE server definition.");
            iceServers.Add(server);
        }

        return new PeerConnection.RTCConfiguration(iceServers);
    }
}
#endif
