using System.Threading.Channels;
using Unswarm.Core.Models;

namespace Unswarm.Core.Services.Scheduler;

/// <summary>
/// Per-runtime execution lane. All requests whose model maps to the same registered
/// runtime (on the same target) flow through one lane: a bounded pending channel fed
/// by the dispatcher, a resident-model/container state, and concurrency counters.
/// The single event-driven scheduler starts lane heads when coexistence, capacity,
/// and skip-budget rules allow; each started item runs as a fire-and-forget task.
/// </summary>
public sealed class RuntimeLane
{
    public required string TargetId { get; init; }

    /// <summary>Registered runtime id this lane is bound to (routing key).</summary>
    public required string RuntimeId { get; init; }

    /// <summary>
    /// Bounded pending queue (depth = clamped MaxQueueDepth, FullMode.Wait).
    /// Written by the dispatcher; drained into the scheduler's ready queue.
    /// </summary>
    public required Channel<QueueItem> Pending { get; init; }

    /// <summary>
    /// Serializes model switches on this lane so concurrent (coexistence-allowed)
    /// requests never mutate container state at the same time. Never replaced —
    /// create once per lane.
    /// </summary>
    public SemaphoreSlim SwitchLock { get; } = new(1, 1);

    /// <summary>Model of the currently active / most-recent request on this lane.</summary>
    public string? ResidentModel { get; set; }

    /// <summary>Docker container id (or "script:&lt;regId&gt;") serving the resident model.</summary>
    public string? ResidentContainerId { get; set; }

    /// <summary>
    /// Maximum concurrent inferences for this lane, derived from
    /// <see cref="RegisteredRuntime.MaxConcurrentInferences"/> when the lane is created.
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>Gates how many requests may execute concurrently on this lane.</summary>
    public SemaphoreSlim? ConcurrencyGate { get; set; }

    /// <summary>
    /// ALL in-flight work counted on this lane. Managed via <see cref="Interlocked"/>.
    /// Incremented synchronously by the scheduler BEFORE launching the runner task so
    /// capacity gates never observe a stale zero.
    /// </summary>
    public int ActiveInferences;

    /// <summary>
    /// How many requests on this lane have been launched while bypassing another
    /// blocked lane head. Bounded by <see cref="SchedulerSettings.ParallelSlotSkipLimit"/>.
    /// </summary>
    public int SkipsUsed;

    /// <summary>
    /// Processed queue items since the last skip-counter reset. When it reaches
    /// <see cref="SchedulerSettings.QueueStepsTillReset"/>, both this counter and
    /// <see cref="SkipsUsed"/> are reset to zero.
    /// </summary>
    public int SequentialStepsProcessed;
}

public sealed record RunningContainerInfo
{
    public required string Key { get; init; }

    /// <summary>Registered runtime id backing this entry (always present — no legacy keys).</summary>
    public required string RegisteredRuntimeId { get; init; }
    public required string ContainerName { get; init; }
    public required string ContainerId { get; init; }
}
