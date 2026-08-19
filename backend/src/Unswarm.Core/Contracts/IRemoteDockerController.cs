using Unswarm.Core.Models;

namespace Unswarm.Core.Contracts;

/// <summary>
/// IDockerController variant for remote agents. Adds agent-mediated health probing,
/// model discovery, and inference proxying (all tunneled over the agent WebSocket).
/// </summary>
public interface IRemoteDockerController : IDockerController
{
    Task<bool> HealthCheckAsync(int port, CancellationToken ct = default);
    Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(int port, CancellationToken ct = default);

    /// <summary>
    /// Runs a chat-completion request against the remote agent's local container.
    /// <paramref name="requestJson"/> is the raw OpenAI chat-completions body; the raw
    /// response body is returned as a string.
    /// </summary>
    Task<string> InferAsync(int port, string requestJson, CancellationToken ct = default);
}
