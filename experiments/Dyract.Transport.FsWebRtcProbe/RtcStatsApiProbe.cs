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

    public static string[] ReadStatShapes(RTCStatsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var map = report.StatsMap;
        if (map is null)
        {
            return [];
        }

        var result = new List<string>(map.Count);
        foreach (var entry in map)
        {
            var stats = entry.Value;
            if (stats is null)
            {
                continue;
            }

            result.Add($"{entry.Key}|{stats.Id}|{stats.Type}|{stats.Members?.Count ?? 0}");
        }

        return result.ToArray();
    }

    public static Java.Lang.Object? ReadMember(RTCStats stats, string memberName)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);
        var members = stats.Members;
        if (members is null || !members.TryGetValue(memberName, out var value))
        {
            return null;
        }

        return value;
    }

    public static double ReadTimestamp(RTCStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        return stats.TimestampUs;
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
