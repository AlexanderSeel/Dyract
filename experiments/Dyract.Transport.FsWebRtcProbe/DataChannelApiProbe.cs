#if ANDROID
using Java.Nio;
using Org.Webrtc;

namespace Dyract.Transport.FsWebRtcProbe;

public static class DataChannelApiProbe
{
    public static void RegisterObserver(
        DataChannel dataChannel,
        DataChannel.IObserver observer)
    {
        ArgumentNullException.ThrowIfNull(dataChannel);
        ArgumentNullException.ThrowIfNull(observer);
        dataChannel.RegisterObserver(observer);
    }

    public static void UnregisterObserver(DataChannel dataChannel)
    {
        ArgumentNullException.ThrowIfNull(dataChannel);
        dataChannel.UnregisterObserver();
    }

    public static bool SendBinary(DataChannel dataChannel, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(dataChannel);
        ArgumentNullException.ThrowIfNull(payload);

        using var byteBuffer = ByteBuffer.Wrap(payload);
        using var buffer = new DataChannel.Buffer(byteBuffer, true);
        return dataChannel.Send(buffer);
    }

    public static byte[] ReadBuffer(DataChannel.Buffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var data = buffer.Data;
        var bytes = new byte[data.Remaining()];
        data.Get(bytes);
        return bytes;
    }

    public static bool IsBinary(DataChannel.Buffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return buffer.Binary;
    }

    public static string ReadCandidate(IceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return $"{candidate.SdpMid}|{candidate.SdpMLineIndex}|{candidate.Sdp}";
    }

    public static string ReadDescription(SessionDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        return $"{description.Type}|{description.Description}";
    }
}
#endif
