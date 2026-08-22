namespace Unswarm.Core.Models;

public sealed class Settings
{
    public int MaxConcurrentModels { get; init; } = 1;
    public string? DefaultModel { get; init; }
    public int RequestTimeout { get; init; } = 120;
    public int HealthCheckInterval { get; init; } = 10;
    public bool AutoShutdownIdle { get; init; } = true;
    public int IdleTimeout { get; init; } = 300;
    public int LogRetention { get; init; } = 168;
    public bool EnableBenchmarking { get; init; } = true;
    /// <summary>"fifo" | "priority"</summary>
    public string PriorityMode { get; init; } = "fifo";
    public bool BatchDrain { get; init; }
    public bool LazyStop { get; init; } = true;
    public int MaxQueueDepth { get; init; } = 32;

    /// <summary>Max distinct execution targets the scheduler may run concurrently (0 = unlimited).</summary>
    public int MaxConcurrentTargets { get; init; }

    public int HealthCheckTimeoutSeconds { get; init; } = 120;

    /// <summary>Max parallel slots the scheduler may skip before giving up on placement (1-1000).</summary>
    public int ParallelSlotSkipLimit { get; init; } = 3;
}
