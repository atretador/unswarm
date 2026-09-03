using Unswarm.Core.Models;

namespace Unswarm.Core.Contracts;

public sealed class ContainerStartResult
{
    public required string ContainerId { get; init; }
    public int? MappedPort { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class ContainerInspectResult
{
    public required string Status { get; init; }
    public int? Pid { get; init; }
    public long MemoryMb { get; init; }
    public double CpuPercent { get; init; }
    public long UptimeSeconds { get; init; }
}

public interface IDockerController
{
    Task<ContainerStartResult> StartContainerAsync(string modelName, CancellationToken ct = default);
    Task<ContainerStartResult> StartRegisteredContainerAsync(
        string registeredContainerId,
        string image,
        int containerPort,
        string? gpuDevices,
        long memoryLimitMb,
        Dictionary<string, string> extraLabels,
        CancellationToken ct = default);
    Task StopContainerAsync(string idOrModel, CancellationToken ct = default);
    Task<ContainerStartResult> RestartContainerAsync(string id, CancellationToken ct = default);
    Task<ContainerInspectResult?> InspectContainerAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetContainerLogsAsync(string id, int tailLines = 100, CancellationToken ct = default);
    Task RemoveContainerAsync(string id, CancellationToken ct = default);
    /// <summary>
    /// Resolves the host-mapped port for a container by inspecting its Docker port bindings.
    /// Returns null when the container is not found or has no port mapping (e.g. host networking).
    /// </summary>
    Task<int?> ResolveMappedPortAsync(string containerName, int containerPort, CancellationToken ct = default);
}
