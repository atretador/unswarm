using Unswarm.Core.Models;

namespace Unswarm.Core.Services.Scheduler;

public sealed class SchedulerSettings
{
    public bool LazyStop { get; init; } = true;
    public bool BatchDrain { get; init; }
    public string PriorityMode { get; init; } = "fifo";
    public int MaxQueueDepth { get; init; } = 32;
    public int RequestTimeout { get; init; } = 120;

    /// <summary>Max distinct execution targets that may run concurrently (0 = unlimited).</summary>
    public int MaxConcurrentTargets { get; init; }

    public int HealthCheckTimeoutSeconds { get; init; } = 120;

    /// <summary>Max retries when a container start fails (1 = no retries, default 3).</summary>
    public int MaxContainerStartRetries { get; init; } = 3;

    /// <summary>Max parallel slots the scheduler may skip before giving up on placement (1-1000).</summary>
    public int ParallelSlotSkipLimit { get; init; } = 3;

    public static SchedulerSettings FromSettings(Settings s) => new()
    {
        LazyStop = s.LazyStop,
        BatchDrain = s.BatchDrain,
        PriorityMode = s.PriorityMode,
        MaxQueueDepth = s.MaxQueueDepth,
        RequestTimeout = s.RequestTimeout,
        MaxConcurrentTargets = s.MaxConcurrentTargets,
        HealthCheckTimeoutSeconds = s.HealthCheckTimeoutSeconds,
        ParallelSlotSkipLimit = Math.Clamp(s.ParallelSlotSkipLimit, 1, 1000)
    };
}
