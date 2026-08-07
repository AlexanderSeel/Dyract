#if ANDROID
using Org.Webrtc;

namespace Dyract.Transport.FsWebRtcProbe;

public static class RtcStatsApiProbe
{
    public static void Request(PeerConnection peerConnection, Action<RTCStatsReport?> callback)
    {
        ArgumentNullException.ThrowIfNull(peerConnection);
        ArgumentNullException.ThrowIfNull(callback);
        peerConnection.GetStats(new Collector(callback));
    }

    public static int ReadReportCount(RTCStatsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.StatsMap?.Count ?? 0;
    }

    public static double ReadTimestamp(RTCStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        return stats.TimestampUs;
    }

    public static int ReadMemberCount(RTCStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        return stats.Members?.Count ?? 0;
    }

    private sealed class Collector : Java.Lang.Object, IRTCStatsCollectorCallback
    {
        private readonly Action<RTCStatsReport?> _callback;

        public Collector(Action<RTCStatsReport?> callback)
        {
            _callback = callback;
        }

        public void OnStatsDelivered(RTCStatsReport? report)
            => _callback(report);
    }
}
#endif
