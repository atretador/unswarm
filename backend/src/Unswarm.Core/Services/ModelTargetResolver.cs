using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Core.Services;

/// <summary>
/// Resolves a model name to its execution target using the container registry.
/// The model's registered container may carry an Agent name; when set (and not
/// "host"), the target is "agent:&lt;name&gt;". Unknown/unassigned models run on "host".
/// </summary>
public sealed class ModelTargetResolver : IModelTargetResolver
{
    private readonly IContainerRegistry _registry;

    public ModelTargetResolver(IContainerRegistry registry)
    {
        _registry = registry;
    }

    public async Task<string> ResolveTargetAsync(string modelName, CancellationToken ct = default)
    {
        var registeredContainerId = await _registry.GetContainerIdForModelAsync(modelName, ct).ConfigureAwait(false);
        if (registeredContainerId is null)
            return ExecutionTarget.HostId;

        var container = await _registry.GetAsync(registeredContainerId, ct).ConfigureAwait(false);
        if (container is null)
            return ExecutionTarget.HostId;

        var agent = container.Agent;
        if (string.IsNullOrWhiteSpace(agent) || agent.Equals(ExecutionTarget.HostId, StringComparison.OrdinalIgnoreCase))
            return ExecutionTarget.HostId;

        return ExecutionTarget.ForAgent(agent).Id;
    }
}
