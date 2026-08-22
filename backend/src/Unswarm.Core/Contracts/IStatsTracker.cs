using Unswarm.Core.Models;

namespace Unswarm.Core.Contracts;

public interface IStatsTracker
{
    void RecordCompletion(InferenceRequest request);
    void RecordError(InferenceRequest request);
    void RecordSwitch(double durationMs);
    void TrackActiveRequest(string requestId);
    void UntrackActiveRequest(string requestId);
    void SetQueueDepthProvider(Func<long> provider);
    Task<StatsSummary> GetSummaryAsync(CancellationToken ct = default);
}
