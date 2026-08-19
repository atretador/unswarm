namespace Unswarm.Core.Contracts;

/// <summary>
/// Resolves the execution target for a model name: "host" (local Docker) or
/// "agent:&lt;name&gt;" when the model's registered container has an Agent assigned.
/// Defaults to "host" when no agent is assigned or the model is unknown.
/// </summary>
public interface IModelTargetResolver
{
    Task<string> ResolveTargetAsync(string modelName, CancellationToken ct = default);
}
