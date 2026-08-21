using Unswarm.Core.Models;

namespace Unswarm.Core.Contracts;

public sealed record ContainerRegistrationRequest
{
    public string DisplayName { get; init; } = string.Empty;
    public required string Image { get; init; }
    public RuntimeKind RuntimeKind { get; init; } = RuntimeKind.Container;
    public string? LauncherPath { get; init; }
    public int ContainerPort { get; init; } = 8080;
    public string? GpuDevices { get; init; }
    public long MemoryLimitMb { get; init; }
    public Dictionary<string, string> ExtraLabels { get; init; } = [];
    /// <summary>Execution target agent name; "host" for local Docker.</summary>
    public string Agent { get; init; } = "host";
    /// <summary>Same-agent container names this container may run concurrently with (empty = alone).</summary>
    public IReadOnlyList<string> CanRunAlongWith { get; init; } = [];
}

public sealed record RegisteredRuntimeWithModels
{
    public required RegisteredRuntime Container { get; init; }
    public required IReadOnlyList<ModelDefinition> DiscoveredModels { get; init; }
}

public interface IContainerRegistrationService
{
    Task<RegisteredRuntimeWithModels> RegisterAsync(ContainerRegistrationRequest request, CancellationToken ct = default);
    Task<RegisteredRuntimeWithModels> StartAsync(string registeredContainerId, CancellationToken ct = default);
    Task<RegisteredRuntimeWithModels> RediscoverAsync(string registeredContainerId, CancellationToken ct = default);
    Task DeleteAsync(string id, bool deleteModels, CancellationToken ct = default);
    Task<RegisteredRuntime?> UpdateCanRunAlongWithAsync(string id, IReadOnlyList<string> canRunAlongWith, CancellationToken ct = default);
    Task<RegisteredRuntime?> StopAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Resolves the LIVE docker container id for a possibly-stale persisted
    /// RuntimeContainerId. When the id belongs to a registered runtime whose container
    /// was recreated (same name, new docker id), the live id is returned AND persisted
    /// back to the registry. Unknown ids and unresolvable runtimes pass through unchanged
    /// so generic (non-registered) docker operations keep their existing behavior.
    /// </summary>
    Task<string> ResolveLiveContainerIdAsync(string runtimeContainerId, CancellationToken ct = default);
}
