using System.Threading.Channels;
using Unswarm.Core.Models;

namespace Unswarm.Core.Services.Scheduler;

/// <summary>
/// Per-target scheduler state. Each execution target ("host" | "agent:&lt;name&gt;")
/// owns a bounded channel, a sequential worker, and single-slot model state.
/// Cross-target requests run concurrently; within a target, requests are processed
/// one at a time and container stop/start switching is scoped to this target only.
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
    public string? ResidentRegisteredContainerId { get; set; }

    /// <summary>
    /// Containers this scheduler has started on the target, keyed by registered container id
    /// (or "legacy:&lt;containerId&gt;" for unregistered models). Used for canRunAlongWith checks.
    /// </summary>
    public Dictionary<string, RunningContainerInfo> RunningContainers { get; } = new(StringComparer.Ordinal);
}

public sealed record RunningContainerInfo
{
    public required string Key { get; init; }
    public string? RegisteredContainerId { get; init; }
    public required string ContainerName { get; init; }
    public required string ContainerId { get; init; }
}
