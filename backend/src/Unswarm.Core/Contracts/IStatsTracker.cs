using Unswarm.Core.Models;

namespace Unswarm.Core.Contracts;

public interface IStatsTracker
{
    void RecordCompletion(InferenceRequest request);
    void RecordError(InferenceRequest request);
    void RecordSwitch(double durationMs);
    Task<StatsSummary> GetSummaryAsync(CancellationToken ct = default);
}
