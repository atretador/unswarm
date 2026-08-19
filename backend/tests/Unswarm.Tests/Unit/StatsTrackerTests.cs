using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class StatsTrackerTests
{
    private readonly FakeClock _clock = new();

    private StatsTracker CreateTracker() => new(_clock);

    private static InferenceRequest MakeRequest(int tokensGenerated = 10)
    {
        var req = new InferenceRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            ModelName = "test",
            OriginalJson = "{}",
            EnqueuedAt = DateTimeOffset.UtcNow,
            Tcs = new TaskCompletionSource<InferenceResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        req.Tcs.TrySetResult(new InferenceResponse { StatusCode = 200, TokensGenerated = tokensGenerated });
        return req;
    }

    [Fact]
    public async Task FreshTracker_ReturnsZeroCounts()
    {
        var tracker = CreateTracker();
        var summary = await tracker.GetSummaryAsync();

        Assert.Equal(0, summary.TotalRequests);
        Assert.Equal(0, summary.TotalTokensProcessed);
        Assert.Equal(0, summary.ErrorsLast24h);
        Assert.Equal(0.0, summary.AvgLatencyMs);
    }

    [Fact]
    public async Task RecordCompletion_IncrementsTotalRequests()
    {
        var tracker = CreateTracker();
        tracker.RecordCompletion(MakeRequest());

        var summary = await tracker.GetSummaryAsync();
        Assert.Equal(1, summary.TotalRequests);
    }

    [Fact]
    public async Task RecordCompletion_AccumulatesTokens()
    {
        var tracker = CreateTracker();
        tracker.RecordCompletion(MakeRequest(tokensGenerated: 10));
        tracker.RecordCompletion(MakeRequest(tokensGenerated: 20));
        tracker.RecordCompletion(MakeRequest(tokensGenerated: 30));

        var summary = await tracker.GetSummaryAsync();
        Assert.Equal(3, summary.TotalRequests);
        Assert.Equal(60, summary.TotalTokensProcessed);
    }

    [Fact]
    public async Task RecordError_IncrementsTotalAndErrors()
    {
        var tracker = CreateTracker();
        tracker.RecordError(MakeRequest());
        tracker.RecordError(MakeRequest());

        var summary = await tracker.GetSummaryAsync();
        Assert.Equal(2, summary.TotalRequests);
        Assert.Equal(2, summary.ErrorsLast24h);
    }

    [Fact]
    public async Task RecordCompletion_ThenError_CombinesCounts()
    {
        var tracker = CreateTracker();
        tracker.RecordCompletion(MakeRequest());
        tracker.RecordCompletion(MakeRequest());
        tracker.RecordError(MakeRequest());

        var summary = await tracker.GetSummaryAsync();
        Assert.Equal(3, summary.TotalRequests);
        Assert.Equal(1, summary.ErrorsLast24h);
    }

    [Fact]
    public async Task Uptime_IncreasesWithClockAdvance()
    {
        var tracker = CreateTracker();

        var s0 = await tracker.GetSummaryAsync();
        Assert.Equal(0, s0.UptimeSeconds);

        _clock.UtcNow = _clock.UtcNow.AddSeconds(120);
        var s1 = await tracker.GetSummaryAsync();
        Assert.True(s1.UptimeSeconds >= 119 && s1.UptimeSeconds <= 121);
    }

    [Fact]
    public async Task MultipleCompletions_AverageLatencyNonZero()
    {
        var tracker = CreateTracker();

        // Use advancing clock to produce measurable latencies
        var t0 = _clock.UtcNow;
        tracker.RecordCompletion(new InferenceRequest
        {
            Id = "a", ModelName = "m", OriginalJson = "{}",
            EnqueuedAt = t0,
            Tcs = new TaskCompletionSource<InferenceResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
        });

        _clock.UtcNow = t0.AddMilliseconds(50);
        tracker.RecordCompletion(new InferenceRequest
        {
            Id = "b", ModelName = "m", OriginalJson = "{}",
            EnqueuedAt = t0,
            Tcs = new TaskCompletionSource<InferenceResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
        });

        var summary = await tracker.GetSummaryAsync();
        Assert.Equal(2, summary.TotalRequests);
        Assert.True(summary.AvgLatencyMs > 0, $"Expected positive avg latency, got {summary.AvgLatencyMs}");
    }

    [Fact]
    public async Task ErrorsAndCompletions_IndependentCounters()
    {
        var tracker = CreateTracker();

        tracker.RecordCompletion(MakeRequest());
        tracker.RecordCompletion(MakeRequest());
        tracker.RecordError(MakeRequest());
        tracker.RecordError(MakeRequest());
        tracker.RecordError(MakeRequest());

        var summary = await tracker.GetSummaryAsync();
        Assert.Equal(5, summary.TotalRequests);
        Assert.Equal(3, summary.ErrorsLast24h);
    }

    [Fact]
    public async Task FreshTracker_ReturnsZeroSwitchMetrics()
    {
        var tracker = CreateTracker();
        var summary = await tracker.GetSummaryAsync();

        Assert.Equal(0, summary.SwitchCount);
        Assert.Equal(0.0, summary.LastSwitchMs);
        Assert.Equal(0.0, summary.AvgSwitchMs);
    }

    [Fact]
    public async Task RecordSwitch_IncrementsCountAndTracksLast()
    {
        var tracker = CreateTracker();
        tracker.RecordSwitch(1500.5);

        var summary = await tracker.GetSummaryAsync();
        Assert.Equal(1, summary.SwitchCount);
        Assert.Equal(1500.5, summary.LastSwitchMs);
        Assert.Equal(1500.5, summary.AvgSwitchMs);
    }

    [Fact]
    public async Task RecordSwitch_ComputesRunningAverage()
    {
        var tracker = CreateTracker();
        tracker.RecordSwitch(1000);
        tracker.RecordSwitch(2000);
        tracker.RecordSwitch(3000);

        var summary = await tracker.GetSummaryAsync();
        Assert.Equal(3, summary.SwitchCount);
        Assert.Equal(3000, summary.LastSwitchMs);
        Assert.Equal(2000.0, summary.AvgSwitchMs);
    }

    [Fact]
    public async Task RecordSwitch_IndependentOfCompletionCounters()
    {
        var tracker = CreateTracker();
        tracker.RecordCompletion(MakeRequest());
        tracker.RecordSwitch(500);
        tracker.RecordError(MakeRequest());

        var summary = await tracker.GetSummaryAsync();
        Assert.Equal(2, summary.TotalRequests);
        Assert.Equal(1, summary.ErrorsLast24h);
        Assert.Equal(1, summary.SwitchCount);
        Assert.Equal(500.0, summary.LastSwitchMs);
    }
}
