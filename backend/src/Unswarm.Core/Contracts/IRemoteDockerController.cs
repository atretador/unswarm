using Unswarm.Core.Models;

namespace Unswarm.Core.Contracts;

/// <summary>
/// IDockerController variant for remote agents. Adds agent-mediated health probing
/// and model discovery (proxied to the agent's local Docker /v1/models endpoint).
/// </summary>
public interface IRemoteDockerController : IDockerController
{
    Task<bool> HealthCheckAsync(int port, CancellationToken ct = default);
    Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(int port, CancellationToken ct = default);
}
