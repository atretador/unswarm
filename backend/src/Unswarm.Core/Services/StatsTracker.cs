namespace Unswarm.Core.Services;

using System.Collections.Concurrent;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

public sealed class StatsTracker : IStatsTracker
{
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<string, RequestRecord> _activeRequests = new();
    private readonly List<double> _latencies = new();
    private long _totalRequests;
    private long _totalTokens;
    private long _totalPromptTokensCached;
    private long _startTimeTicks;
    private int _switchCount;
    private double _lastSwitchMs;
    private double _avgSwitchMs;
    private readonly ConcurrentQueue<(DateTimeOffset Time, long Tokens)> _recentCompletions = new();
    private readonly ConcurrentQueue<DateTimeOffset> _recentErrors = new();
    private Func<long>? _queueDepthProvider;
    private readonly object _lock = new();

    public StatsTracker(IClock clock)
    {
        _clock = clock;
        _startTimeTicks = _clock.UtcNow.Ticks;
    }

    public void TrackActiveRequest(string requestId)
    {
        _activeRequests.TryAdd(requestId, new RequestRecord { StartedAt = _clock.UtcNow });
    }

    public void UntrackActiveRequest(string requestId)
    {
        _activeRequests.TryRemove(requestId, out _);
    }

    public void SetQueueDepthProvider(Func<long> provider)
    {
        _queueDepthProvider = provider;
    }

    public void RecordCompletion(InferenceRequest request)
    {
        var elapsed = (long)(_clock.UtcNow - request.EnqueuedAt).TotalMilliseconds;
        lock (_lock)
        {
            _latencies.Add(elapsed);
            if (_latencies.Count > 10000) _latencies.RemoveAt(0);
        }

        Interlocked.Increment(ref _totalRequests);

        int tokens = 0;
        int cachedTokens = 0;
        if (request.Tcs.Task.IsCompletedSuccessfully)
        {
            tokens = request.Tcs.Task.Result.TokensGenerated;
            cachedTokens = request.Tcs.Task.Result.PromptTokensCached;
        }
        Interlocked.Add(ref _totalTokens, tokens);
        Interlocked.Add(ref _totalPromptTokensCached, cachedTokens);

        _recentCompletions.Enqueue((_clock.UtcNow, tokens));
        PruneRecentCompletions();
    }

    public void RecordError(InferenceRequest request)
    {
        Interlocked.Increment(ref _totalRequests);
        _recentErrors.Enqueue(_clock.UtcNow);
        PruneRecentErrors();
    }

    public void RecordSwitch(double durationMs)
    {
        lock (_lock)
        {
            _switchCount++;
            _lastSwitchMs = durationMs;
            // Running average: newAvg = oldAvg + (newVal - oldAvg) / count
            _avgSwitchMs += (durationMs - _avgSwitchMs) / _switchCount;
        }
    }

    public Task<StatsSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var uptime = (long)(_clock.UtcNow.Ticks - _startTimeTicks) / TimeSpan.TicksPerSecond;
        var now = _clock.UtcNow;

        double[] rpm;
        double[] tps;
        lock (_lock)
        {
            rpm = ComputePerMinute(now);
            tps = ComputeTokensPerSecond(now);
        }

        // Prune errors before counting to ensure accuracy
        PruneRecentErrors();

        var summary = new StatsSummary
        {
            TotalRequests = Volatile.Read(ref _totalRequests),
            ActiveRequests = _activeRequests.Count,
            AvgLatencyMs = _latencies.Count > 0 ? _latencies.Average() : 0,
            TotalTokensProcessed = Volatile.Read(ref _totalTokens),
            TotalPromptTokensCached = Volatile.Read(ref _totalPromptTokensCached),
            UptimeSeconds = uptime,
            RequestsPerMinute = rpm,
            ErrorsLast24h = _recentErrors.Count,
            TokensPerSecond = tps,
            QueueDepth = (int)(_queueDepthProvider?.Invoke() ?? 0),
            SwitchCount = _switchCount,
            LastSwitchMs = _lastSwitchMs,
            AvgSwitchMs = _avgSwitchMs
        };

        return Task.FromResult(summary);
    }

    private void PruneRecentCompletions()
    {
        var cutoff = _clock.UtcNow.AddHours(-1);
        while (_recentCompletions.TryPeek(out var item) && item.Time < cutoff)
        {
            _recentCompletions.TryDequeue(out _);
        }
    }

    private void PruneRecentErrors()
    {
        var cutoff = _clock.UtcNow.AddHours(-24);
        while (_recentErrors.TryPeek(out var time) && time < cutoff)
        {
            _recentErrors.TryDequeue(out _);
        }
    }

    private double[] ComputePerMinute(DateTimeOffset now)
    {
        var result = new double[60];
        for (int i = 0; i < 60; i++)
        {
            var start = now.AddMinutes(-60 + i);
            var end = start.AddMinutes(1);
            result[i] = _recentCompletions.Count(c => c.Time >= start && c.Time < end);
        }
        return result;
    }

    private double[] ComputeTokensPerSecond(DateTimeOffset now)
    {
        var result = new double[60];
        for (int i = 0; i < 60; i++)
        {
            var start = now.AddSeconds(-60 + i);
            var end = start.AddSeconds(1);
            var tokensInWindow = _recentCompletions
                .Where(c => c.Time >= start && c.Time < end)
                .Sum(c => c.Tokens);
            result[i] = tokensInWindow;
        }
        return result;
    }

    private sealed class RequestRecord
    {
        public DateTimeOffset StartedAt { get; init; }
    }
}
