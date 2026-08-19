namespace Unswarm.Core.Models;

public sealed class InferenceResponse
{
    public int StatusCode { get; init; }
    public string ContentType { get; init; } = "application/json";
    public Stream? Body { get; init; }
    public int TokensGenerated { get; init; }
}
