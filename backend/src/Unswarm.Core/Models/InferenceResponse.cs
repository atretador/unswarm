namespace Unswarm.Core.Models;

public sealed class InferenceResponse
{
    public int StatusCode { get; init; }
    public string ContentType { get; init; } = "application/json";
    public Stream? Body { get; init; }

    /// <summary>
    /// Token counts may be updated incrementally by the streaming token tap.
    /// The scheduler reads these only after <see cref="BodyDrained"/> completes,
    /// so they are guaranteed to be final.
    /// </summary>
    public int TokensGenerated { get; set; }
    public double ServerTokensPerSec { get; set; }

    /// <summary>
    /// When non-null, the scheduler must await this task before considering the
    /// request fully complete. This prevents the per-target worker from dequeuing
    /// the next request (and potentially switching models) while the upstream
    /// body is still being consumed by the API controller.
    /// </summary>
    public Task? BodyDrained { get; init; }
}
