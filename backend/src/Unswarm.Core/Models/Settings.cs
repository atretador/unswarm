namespace Unswarm.Core.Models;

public sealed class Settings
{
    public int RequestTimeout { get; init; } = 120;
    public int HealthCheckInterval { get; init; } = 10;
    public bool AutoShutdownIdle { get; init; } = false;
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

    /// <summary>Whether the scheduler may skip parallel slots when placing queue items.</summary>
    public bool EnableParallelSlotSkip { get; init; }

    /// <summary>How many queue items are processed before the per-target parallel-slot skip counter resets.</summary>
    public int QueueStepsTillReset { get; init; } = 3;

    /// <summary>
    /// Whether recently-active conversations hold their runtime against eviction
    /// for the dwell window (tool-call-loop thrash protection).
    /// </summary>
    public bool EnableConversationAffinity { get; init; }

    /// <summary>How long a conversation keeps its runtime held after its last request (seconds).</summary>
    public int ConversationDwellSeconds { get; init; } = 45;

    /// <summary>
    /// When true, hides the "cloud/" or "managed/" origin prefix from model display names.
    /// </summary>
    public bool HideOriginPrefix { get; init; }

    /// <summary>
    /// JSON map of agent names to user-chosen display names. E.g. {"host": "My Workstation"}.
    /// </summary>
    public string AgentDisplayNames { get; init; } = "{}";

    /// <summary>Usage records older than this many days are eligible for purge (0 = keep forever).</summary>
    public int UsageRetentionDays { get; init; } = 30;

    /// <summary>
    /// JSON map of provider name to monthly budget object, e.g.
    /// {"cloud":{"tokenBudget":1000000,"costBudget":25.0}}.
    /// </summary>
    public string ProviderBudgetsJson { get; init; } = "{}";
}
