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

    /// <summary>
    /// Registered runtime id resolved ONCE by the scheduler at dispatch time and
    /// carried through to the inference proxy, so the proxy never re-queries the
    /// model→runtime mapping per request. Null when the dispatcher did not resolve
    /// it (e.g. direct proxy use outside the scheduler).
    /// </summary>
    public string? ResolvedRuntimeId { get; set; }

    /// <summary>
    /// The registered runtime entity matching <see cref="ResolvedRuntimeId"/>,
    /// resolved at dispatch time and carried through to the inference proxy.
    /// </summary>
    public RegisteredRuntime? ResolvedRuntime { get; set; }

    /// <summary>
    /// Stable fingerprint grouping consecutive tool-call-loop requests into one
    /// conversation ("sid:&lt;value&gt;" or "conv:&lt;sha256&gt;"). Null when no
    /// affinity signal exists. Used by the scheduler to hold a runtime against
    /// eviction while its conversation is recently active.
    /// </summary>
    public string? ConversationKey { get; set; }
}
