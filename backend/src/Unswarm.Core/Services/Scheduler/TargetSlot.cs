using System.Threading;
using System.Threading.Channels;
using Unswarm.Core.Models;

namespace Unswarm.Core.Services.Scheduler;

/// <summary>
/// Per-target scheduler state. Each execution target ("host" | "agent:&lt;name&gt;")
/// owns a bounded channel, a sequential worker, and single-slot model state.
/// Cross-target requests run concurrently; within a target, requests are processed
/// one at a time and container stop/start switching is scoped to this target only.
/// When <see cref="MaxConcurrency"/> &gt; 1, multiple requests for the same model may
/// be processed concurrently, gated by <see cref="ConcurrencyGate"/>.
/// </summary>
public sealed class TargetSlot
{
    public required string TargetId { get; init; }
    public required Channel<InferenceRequest> Channel { get; init; }

    /// <summary>Task backing this target's sequential worker (started on demand).</summary>
    public Task? Worker { get; set; }

    /// <summary>Model of the currently active / most-recent request on this target.</summary>
    public string? ResidentModel { get; set; }

    /// <summary>Docker container id serving the resident model.</summary>
    public string? ResidentContainerId { get; set; }

    /// <summary>Registered container id the resident model maps to (null for legacy models).</summary>
    public string? ResidentRegisteredRuntimeId { get; set; }

    /// <summary>
    /// Containers this scheduler has started on the target, keyed by registered container id
    /// (or "legacy:&lt;containerId&gt;" for unregistered models). Used for canRunAlongWith checks.
    /// </summary>
    public Dictionary<string, RunningContainerInfo> RunningContainers { get; } = new(StringComparer.Ordinal);

    // ── Concurrency control ────────────────────────────────────────────────

    /// <summary>
    /// Maximum concurrent inferences for this target, derived from
    /// <see cref="RegisteredRuntime.MaxConcurrentInferences"/> when the slot is created.
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>
    /// Gates how many requests may execute concurrently on this target.
    /// Initialized to <c>new SemaphoreSlim(MaxConcurrency, MaxConcurrency)</c>.
    /// </summary>
    public SemaphoreSlim? ConcurrencyGate { get; set; }

    /// <summary>
    /// Number of inferences currently in-flight on this target.
    /// Managed via <see cref="Interlocked"/> for thread-safe increment/decrement.
    /// Used by the parallel dispatcher to decide when to wait for drain
    /// before switching models.
    /// </summary>
    public int ActiveInferences;

    /// <summary>
    /// How many same-model requests have been "skipped" (launched concurrently)
    /// since the last model switch or limit reset. Bounded by
    /// <see cref="SchedulerSettings.ParallelSlotSkipLimit"/>.
    /// </summary>
    public int SkipsUsed;

    /// <summary>
    /// Sequentially processed queue items since the last skip-counter reset.
    /// When it reaches <see cref="SchedulerSettings.QueueStepsTillReset"/>, both
    /// this counter and <see cref="SkipsUsed"/> are reset to zero.
    /// </summary>
    public int SequentialStepsProcessed;

    /// <summary>
    /// Serializes model switches on this target so concurrent (coexistence-allowed)
    /// requests never mutate container state at the same time. Unlike
    /// <see cref="ConcurrencyGate"/> it is never replaced — create once per slot.
    /// </summary>
    public SemaphoreSlim SwitchLock { get; } = new(1, 1);
}

public sealed record RunningContainerInfo
{
    public required string Key { get; init; }
    public string? RegisteredRuntimeId { get; init; }
    public required string ContainerName { get; init; }
    public required string ContainerId { get; init; }
}
