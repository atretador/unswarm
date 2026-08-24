using Unswarm.Core.Models;

namespace Unswarm.Api.Dtos;

/// <summary>
/// Dashboard statistics summary: request counts, token processing, latency,
/// queue depth, model/container counts, and time-series metrics.
/// </summary>
public sealed class StatsSummaryResponse
{
    public long TotalRequests { get; set; }
    public int ActiveRequests { get; set; }
    public double AvgLatencyMs { get; set; }
    public long TotalTokensProcessed { get; set; }
    public long TotalPromptTokensCached { get; set; }
    public long UptimeSeconds { get; set; }
    public int ModelsLoaded { get; set; }
    public int ContainersRunning { get; set; }
    public int QueueDepth { get; set; }
    public double[] RequestsPerMinute { get; set; } = [];
    public int ErrorsLast24h { get; set; }
    public double[] TokensPerSecond { get; set; } = [];
    public int SwitchCount { get; set; }
    public double LastSwitchMs { get; set; }
    public double AvgSwitchMs { get; set; }

    public static StatsSummaryResponse FromSummary(StatsSummary s) => new()
    {
        TotalRequests = s.TotalRequests,
        ActiveRequests = s.ActiveRequests,
        AvgLatencyMs = s.AvgLatencyMs,
        TotalTokensProcessed = s.TotalTokensProcessed,
        TotalPromptTokensCached = s.TotalPromptTokensCached,
        UptimeSeconds = s.UptimeSeconds,
        ModelsLoaded = s.ModelsLoaded,
        ContainersRunning = s.ContainersRunning,
        QueueDepth = s.QueueDepth,
        RequestsPerMinute = s.RequestsPerMinute,
        ErrorsLast24h = s.ErrorsLast24h,
        TokensPerSecond = s.TokensPerSecond,
        SwitchCount = s.SwitchCount,
        LastSwitchMs = s.LastSwitchMs,
        AvgSwitchMs = s.AvgSwitchMs
    };
}
