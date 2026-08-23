using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.E2ETests.Fakes;

public sealed class FakeStatsTracker : IStatsTracker
{
    private int _completionCount;
    private int _errorCount;
    private int _switchCount;

    public int CompletionCount => Volatile.Read(ref _completionCount);
    public int ErrorCount => Volatile.Read(ref _errorCount);
    public int SwitchCount => Volatile.Read(ref _switchCount);
    public List<InferenceRequest> CompletedRequests { get; } = [];
    public List<InferenceRequest> ErrorRequests { get; } = [];

    public StatsSummary SummaryToReturn { get; set; } = new();

    public void RecordCompletion(InferenceRequest request)
    {
        Interlocked.Increment(ref _completionCount);
        lock (CompletedRequests) CompletedRequests.Add(request);
    }

    public void RecordError(InferenceRequest request)
    {
        Interlocked.Increment(ref _errorCount);
        lock (ErrorRequests) ErrorRequests.Add(request);
    }

    public void RecordSwitch(double durationMs) => Interlocked.Increment(ref _switchCount);

    public void TrackActiveRequest(string requestId) { }
    public void UntrackActiveRequest(string requestId) { }
    public void SetQueueDepthProvider(Func<long> provider) { }

    public Task<StatsSummary> GetSummaryAsync(CancellationToken ct = default)
        => Task.FromResult(SummaryToReturn);
}
