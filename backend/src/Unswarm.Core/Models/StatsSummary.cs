namespace Unswarm.Core.Models;

public sealed class StatsSummary
{
    public long TotalRequests { get; init; }
    public int ActiveRequests { get; init; }
    public double AvgLatencyMs { get; init; }
    public long TotalTokensProcessed { get; init; }
    public long TotalPromptTokensCached { get; init; }
    public long UptimeSeconds { get; init; }
    public int ModelsLoaded { get; init; }
    public int ContainersRunning { get; init; }
    public int QueueDepth { get; init; }
    public double[] RequestsPerMinute { get; init; } = [];
    public int ErrorsLast24h { get; init; }
    public double[] TokensPerSecond { get; init; } = [];
    public int SwitchCount { get; init; }
    public double LastSwitchMs { get; init; }
    public double AvgSwitchMs { get; init; }
}
