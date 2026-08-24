namespace Unswarm.Core.Services;

using System.Collections.Concurrent;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

public sealed class StatsTracker : IStatsTracker
{
    /// <summary>Max retained latency samples (ring buffer capacity).</summary>
    private const int LatencyCapacity = 10000;

    /// <summary>
    /// Sliding-window granularity: one slot per second over a 1-hour horizon.
    /// Per-minute/per-second series are aggregated from these O(1)-update slots
    /// instead of scanning an unbounded completion list (was O(60·n)).
    /// </summary>
    private const int SecondSlots = 3600;

    private readonly IClock _clock;
    private readonly ConcurrentDictionary<string, RequestRecord> _activeRequests = new();

    // Latency ring buffer: fixed array + head/count; the oldest sample is
    // overwritten in place once the cap is reached (no O(n) RemoveAt(0)).
    private readonly double[] _latencyRing = new double[LatencyCapacity];
    private int _latencyHead;
    private int _latencyCount;

    private long _totalRequests;
    private long _totalTokens;
    private long _totalPromptTokensCached;
    private long _startTimeTicks;
    private int _switchCount;
    private double _lastSwitchMs;
    private double _avgSwitchMs;
    private readonly ConcurrentQueue<DateTimeOffset> _recentErrors = new();
    private Func<long>? _queueDepthProvider;
    private readonly object _lock = new();

    // Per-second completion counters keyed by absolute Unix second. Slot index is
    // second % SecondSlots; a stale stamp means the slot belongs to a previous
    // hour and is reset on first touch.
    private readonly long[] _secondStamps = new long[SecondSlots];
    private readonly long[] _secondCounts = new long[SecondSlots];
    private readonly long[] _secondTokens = new long[SecondSlots];

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
        var now = _clock.UtcNow;
        var elapsed = (long)(now - request.EnqueuedAt).TotalMilliseconds;

        int tokens = 0;
        int cachedTokens = 0;
        if (request.Tcs.Task.IsCompletedSuccessfully)
        {
            tokens = request.Tcs.Task.Result.TokensGenerated;
            cachedTokens = request.Tcs.Task.Result.PromptTokensCached;
        }

        long second = now.UtcTicks / TimeSpan.TicksPerSecond;
        lock (_lock)
        {
            // Ring buffer insert: overwrite the oldest sample once full.
            _latencyRing[_latencyHead] = elapsed;
            _latencyHead = (_latencyHead + 1) % LatencyCapacity;
            if (_latencyCount < LatencyCapacity)
                _latencyCount++;

            var slot = (int)(second % SecondSlots);
            if (_secondStamps[slot] != second)
            {
                _secondStamps[slot] = second;
                _secondCounts[slot] = 0;
                _secondTokens[slot] = 0;
            }
            _secondCounts[slot]++;
            _secondTokens[slot] += tokens;
        }

        Interlocked.Increment(ref _totalRequests);
        Interlocked.Add(ref _totalTokens, tokens);
        Interlocked.Add(ref _totalPromptTokensCached, cachedTokens);
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

        double avgLatency;
        double[] rpm;
        double[] tps;
        lock (_lock)
        {
            avgLatency = ComputeAverageLatency();
            rpm = ComputePerMinute(now);
            tps = ComputeTokensPerSecond(now);
        }

        // Prune errors before counting to ensure accuracy
        PruneRecentErrors();

        var summary = new StatsSummary
        {
            TotalRequests = Volatile.Read(ref _totalRequests),
            ActiveRequests = _activeRequests.Count,
            AvgLatencyMs = avgLatency,
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

    private void PruneRecentErrors()
    {
        var cutoff = _clock.UtcNow.AddHours(-24);
        while (_recentErrors.TryPeek(out var time) && time < cutoff)
        {
            _recentErrors.TryDequeue(out _);
        }
    }

    private double ComputeAverageLatency()
    {
        if (_latencyCount == 0)
            return 0;
        double sum = 0;
        for (var i = 0; i < _latencyCount; i++)
            sum += _latencyRing[i];
        return sum / _latencyCount;
    }

    /// <summary>
    /// Requests per minute for the last 60 wall-clock minutes, oldest first.
    /// Aggregated from the per-second slots by "seconds ago" — O(SecondSlots).
    /// </summary>
    private double[] ComputePerMinute(DateTimeOffset now)
    {
        var result = new double[60];
        var nowSecond = now.UtcTicks / TimeSpan.TicksPerSecond;
        for (var s = 0; s < SecondSlots; s++)
        {
            var stamp = _secondStamps[s];
            if (stamp == 0)
                continue;
            var age = nowSecond - stamp;
            if (age < 0 || age >= SecondSlots)
                continue;
            result[59 - age / 60] += _secondCounts[s];
        }
        return result;
    }

    /// <summary>
    /// Tokens generated per second for the last 60 seconds, oldest first.
    /// Aggregated from the per-second slots by "seconds ago" — O(SecondSlots).
    /// </summary>
    private double[] ComputeTokensPerSecond(DateTimeOffset now)
    {
        var result = new double[60];
        var nowSecond = now.UtcTicks / TimeSpan.TicksPerSecond;
        for (var s = 0; s < SecondSlots; s++)
        {
            var stamp = _secondStamps[s];
            if (stamp == 0)
                continue;
            var age = nowSecond - stamp;
            if (age < 0 || age >= 60)
                continue;
            result[59 - age] += _secondTokens[s];
        }
        return result;
    }

    private sealed class RequestRecord
    {
        public DateTimeOffset StartedAt { get; init; }
    }
}
