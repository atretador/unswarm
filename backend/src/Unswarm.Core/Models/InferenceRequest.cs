namespace Unswarm.Core.Models;

public sealed class InferenceRequest
{
    public required string Id { get; init; }
    public required string ModelName { get; init; }
    public required string OriginalJson { get; init; }
    public bool IsStreaming { get; init; }
    public int Priority { get; init; }
    public DateTimeOffset EnqueuedAt { get; init; }
    public required TaskCompletionSource<InferenceResponse> Tcs { get; init; }
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Execution target resolved by the scheduler: "host" | "agent:&lt;name&gt;".
    /// Set after dispatch; consumed by the inference proxy for container lookup.
    /// </summary>
    public string? TargetId { get; set; }
}
