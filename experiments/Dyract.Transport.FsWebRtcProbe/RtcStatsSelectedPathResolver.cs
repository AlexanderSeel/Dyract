#if ANDROID
using System.Collections.Concurrent;
using Dyract.Transport;
using Org.Webrtc;

namespace Dyract.Transport.FsWebRtcProbe;

internal static class RtcStatsSelectedPathResolver
{
    private const string TransportStatsType = "transport";
    private const string CandidatePairStatsType = "candidate-pair";
    private const string LocalCandidateStatsType = "local-candidate";
    private const string RemoteCandidateStatsType = "remote-candidate";

    public static SelectedIcePathPrivacySummary? Resolve(RTCStatsReport? report)
    {
        var statsMap = report?.StatsMap;
        if (statsMap is null || statsMap.Count == 0)
        {
            return null;
        }

        RTCStats? selectedPair = null;
        foreach (var entry in statsMap)
        {
            var stats = entry.Value;
            if (stats is null ||
                !string.Equals(stats.Type, TransportStatsType, StringComparison.Ordinal) ||
                !TryReadMember(stats, "selectedCandidatePairId", out var selectedPairId))
            {
                continue;
            }

            selectedPair = FindById(statsMap, selectedPairId);
            if (selectedPair is not null &&
                string.Equals(selectedPair.Type, CandidatePairStatsType, StringComparison.Ordinal))
            {
                break;
            }

            selectedPair = null;
        }

        if (selectedPair is null ||
            !TryReadMember(selectedPair, "localCandidateId", out var localCandidateId) ||
            !TryReadMember(selectedPair, "remoteCandidateId", out var remoteCandidateId))
        {
            return null;
        }

        var localCandidate = FindById(statsMap, localCandidateId);
        var remoteCandidate = FindById(statsMap, remoteCandidateId);
        if (localCandidate is null || remoteCandidate is null ||
            !string.Equals(localCandidate.Type, LocalCandidateStatsType, StringComparison.Ordinal) ||
            !string.Equals(remoteCandidate.Type, RemoteCandidateStatsType, StringComparison.Ordinal) ||
            !TryReadCandidateSummary(localCandidate, out var localSummary) ||
            !TryReadCandidateSummary(remoteCandidate, out var remoteSummary))
        {
            return null;
        }

        return new SelectedIcePathPrivacySummary(localSummary, remoteSummary);
    }

    private static RTCStats? FindById(
        IDictionary<string, RTCStats> statsMap,
        string id)
    {
        if (statsMap.TryGetValue(id, out var exact) && exact is not null)
        {
            return exact;
        }

        foreach (var entry in statsMap)
        {
            var stats = entry.Value;
            if (stats is not null && string.Equals(stats.Id, id, StringComparison.Ordinal))
            {
                return stats;
            }
        }

        return null;
    }

    private static bool TryReadCandidateSummary(
        RTCStats stats,
        out IceCandidatePrivacySummary summary)
    {
        summary = default;
        return TryReadMember(stats, "candidateType", out var candidateType) &&
               TryReadMember(stats, "protocol", out var protocol) &&
               IceCandidatePrivacySummary.TryCreate(candidateType, protocol, out summary);
    }

    private static bool TryReadMember(
        RTCStats stats,
        string memberName,
        out string value)
    {
        value = string.Empty;
        var members = stats.Members;
        if (members is null ||
            !members.TryGetValue(memberName, out var member) ||
            member is null)
        {
            return false;
        }

        var text = member.ToString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256)
        {
            return false;
        }

        value = text;
        return true;
    }
}

internal sealed class SelectedIcePathStatsCollector : Java.Lang.Object, IRTCStatsCollectorCallback
{
    private static readonly ConcurrentDictionary<int, SelectedIcePathStatsCollector> RetainedCollectors = new();
    private static int _nextRetentionId;

    private readonly TaskCompletionSource<SelectedIcePathPrivacySummary?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _retentionId;
    private int _released;

    public SelectedIcePathStatsCollector()
    {
        _retentionId = Interlocked.Increment(ref _nextRetentionId);
        if (!RetainedCollectors.TryAdd(_retentionId, this))
        {
            throw new InvalidOperationException("Could not retain the WebRTC stats callback.");
        }
    }

    public Task<SelectedIcePathPrivacySummary?> Task => _completion.Task;

    public void OnStatsDelivered(RTCStatsReport? report)
    {
        try
        {
            _completion.TrySetResult(RtcStatsSelectedPathResolver.Resolve(report));
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }
        finally
        {
            ReleaseRetention();
        }
    }

    public void ReleaseRejectedRequest()
    {
        ReleaseRetention();
        Dispose();
    }

    private void ReleaseRetention()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        RetainedCollectors.TryRemove(_retentionId, out _);
    }
}
#endif
