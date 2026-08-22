using System.Collections.Concurrent;

namespace Unswarm.Core.Services.Scheduler;

/// <summary>
/// Per-target grouping of runtime lanes. Each execution target ("host" | "agent:&lt;name&gt;")
/// owns one <see cref="TargetGroup"/>; every registered runtime that runs on the target
/// gets its own <see cref="RuntimeLane"/> inside it. Lanes on different targets (and
/// coexistence-compatible lanes on the same target) run concurrently.
/// </summary>
public sealed class TargetGroup
{
    public required string TargetId { get; init; }

    /// <summary>Lanes on this target keyed by registered runtime id.</summary>
    public ConcurrentDictionary<string, RuntimeLane> Lanes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Containers this scheduler has started ON THIS TARGET, keyed by registered
    /// runtime id. Target-scoped (not per-lane): containers are shared resources of
    /// the target, and a lane's model switch must see (and stop) containers started
    /// by sibling lanes. Concurrently mutated by sibling-lane switches — hence the
    /// concurrent dictionary.
    /// </summary>
    public ConcurrentDictionary<string, RunningContainerInfo> RunningContainers { get; } = new(StringComparer.Ordinal);
}
