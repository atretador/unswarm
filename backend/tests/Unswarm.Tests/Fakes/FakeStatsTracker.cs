using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Fakes;

public sealed class FakeStatsTracker : IStatsTracker
{
    private int _completionCount;
    private int _errorCount;
    private int _switchCount;
    private int _activeCount;

    public int CompletionCount => Volatile.Read(ref _completionCount);
    public int ErrorCount => Volatile.Read(ref _errorCount);
    public int SwitchCount => Volatile.Read(ref _switchCount);
    public int ActiveCount => Volatile.Read(ref _activeCount);
    public List<InferenceRequest> CompletedRequests { get; } = [];
    public List<InferenceRequest> ErrorRequests { get; } = [];
    public List<double> SwitchDurations { get; } = [];

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

    public void RecordSwitch(double durationMs)
    {
        Interlocked.Increment(ref _switchCount);
        lock (SwitchDurations) SwitchDurations.Add(durationMs);
    }

    public void TrackActiveRequest(string requestId)
    {
        Interlocked.Increment(ref _activeCount);
    }

    public void UntrackActiveRequest(string requestId)
    {
        Interlocked.Decrement(ref _activeCount);
    }

    public void SetQueueDepthProvider(Func<long> provider) { }

    public Task<StatsSummary> GetSummaryAsync(CancellationToken ct = default)
        => Task.FromResult(SummaryToReturn);
}
