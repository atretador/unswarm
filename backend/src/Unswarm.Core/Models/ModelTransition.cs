namespace Unswarm.Core.Models;

/// <summary>A runtime (and its resident model) affected by a model transition.</summary>
public sealed record RuntimeModelChange(string RuntimeId, string Model);

public sealed record ModelTransition
{
    public required string Id { get; init; }
    /// <summary>Primary model being replaced, or null when nothing is stopped/replaced (cold start or coexistence start).</summary>
    public string? FromModel { get; init; }
    public required string ToModel { get; init; }
    /// <summary>All runtimes (with their resident models) this transition stops.</summary>
    public IReadOnlyList<RuntimeModelChange> Stopping { get; init; } = [];
    /// <summary>Registered runtime id the switch targets (when known).</summary>
    public string? RuntimeId { get; init; }
    /// <summary>"draining" | "switching" | "starting" | "complete"</summary>
    public required string Status { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EstimatedCompletion { get; init; }
}
