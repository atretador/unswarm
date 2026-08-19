using Unswarm.Core.Models;

namespace Unswarm.Core.Contracts;

public sealed record ContainerRegistrationRequest
{
    public string DisplayName { get; init; } = string.Empty;
    public required string Image { get; init; }
    public int ContainerPort { get; init; } = 8080;
    public string? GpuDevices { get; init; }
    public long MemoryLimitMb { get; init; }
    public Dictionary<string, string> ExtraLabels { get; init; } = [];
    /// <summary>Execution target agent name; "host" for local Docker.</summary>
    public string Agent { get; init; } = "host";
    /// <summary>Same-agent container names this container may run concurrently with (empty = alone).</summary>
    public IReadOnlyList<string> CanRunAlongWith { get; init; } = [];
}

public sealed record RegisteredContainerWithModels
{
    public required RegisteredContainer Container { get; init; }
    public required IReadOnlyList<ModelDefinition> DiscoveredModels { get; init; }
}

public interface IContainerRegistrationService
{
    Task<RegisteredContainerWithModels> RegisterAsync(ContainerRegistrationRequest request, CancellationToken ct = default);
    Task<RegisteredContainerWithModels> RediscoverAsync(string registeredContainerId, CancellationToken ct = default);
    Task DeleteAsync(string id, bool deleteModels, CancellationToken ct = default);
}
