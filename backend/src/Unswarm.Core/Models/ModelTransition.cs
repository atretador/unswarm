namespace Unswarm.Core.Models;

public sealed record ModelTransition
{
    public required string Id { get; init; }
    public required string FromModel { get; init; }
    public required string ToModel { get; init; }
    /// <summary>"draining" | "switching" | "starting" | "complete"</summary>
    public required string Status { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EstimatedCompletion { get; init; }
}
