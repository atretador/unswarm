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

    public static SchedulerSettings FromSettings(Settings s) => new()
    {
        LazyStop = s.LazyStop,
        BatchDrain = s.BatchDrain,
        PriorityMode = s.PriorityMode,
        MaxQueueDepth = s.MaxQueueDepth,
        RequestTimeout = s.RequestTimeout,
        MaxConcurrentTargets = s.MaxConcurrentTargets
    };
}
