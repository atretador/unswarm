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

    /// <summary>
    /// Recently-active conversations on this target, keyed by conversation key.
    /// A conversation whose <see cref="ConversationActivity.LastSeenUtc"/> is within
    /// the dwell window HOLDS the runtime that served it against eviction. Entries
    /// are pruned opportunistically when they age out of the dwell window.
    /// </summary>
    public ConcurrentDictionary<string, ConversationActivity> RecentConversations { get; } =
        new(StringComparer.Ordinal);
}

/// <summary>
/// Per-conversation activity record: last time a request belonging to the
/// conversation completed on this target, how many requests have completed, and
/// the registered runtime id currently hosting it. Mutated concurrently by lane
/// runners — <see cref="RequestCount"/> is only ever touched via Interlocked.
/// </summary>
public sealed class ConversationActivity
{
    public DateTime LastSeenUtc;
    public int RequestCount;
    public string RuntimeId = "";
}
