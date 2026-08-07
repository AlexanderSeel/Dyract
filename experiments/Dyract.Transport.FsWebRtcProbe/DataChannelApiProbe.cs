#if ANDROID
using Java.Nio;
using Org.Webrtc;

namespace Dyract.Transport.FsWebRtcProbe;

public static class DataChannelApiProbe
{
    public static void RegisterObserver(DataChannel dataChannel, DataChannel.IObserver observer)
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

    public static string ReadStateName(DataChannel dataChannel)
    {
        ArgumentNullException.ThrowIfNull(dataChannel);
        return dataChannel.InvokeState()?.ToString() ?? string.Empty;
    }

    public static bool IsOpen(DataChannel dataChannel)
    {
        ArgumentNullException.ThrowIfNull(dataChannel);
        return dataChannel.InvokeState() == DataChannel.State.Open;
    }

    public static bool IsClosingOrClosed(DataChannel dataChannel)
    {
        ArgumentNullException.ThrowIfNull(dataChannel);
        var state = dataChannel.InvokeState();
        return state == DataChannel.State.Closing || state == DataChannel.State.Closed;
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
        var data = buffer.Data ?? throw new InvalidOperationException("DataChannel buffer did not contain a native ByteBuffer.");
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
        return description.Description ?? string.Empty;
    }
}
#endif
