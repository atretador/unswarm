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

public sealed class ContainerCreateResult
{
    public required string ContainerId { get; init; }
    public int? MappedPort { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class ContainerCreateConfig
{
    public required string Image { get; init; }
    public required string ContainerName { get; init; }
    public int ContainerPort { get; init; } = 8080;
    public int? HostPort { get; init; }
    public IReadOnlyList<string>? Devices { get; init; }
    public IReadOnlyList<VolumeMount>? Volumes { get; init; }
    public IReadOnlyList<EnvVar>? Env { get; init; }
    public int ShmSizeMb { get; init; } = 16384;
    public string IpcMode { get; init; } = "private";
    public string NetworkMode { get; init; } = "bridge";
    public string RestartPolicy { get; init; } = "unless-stopped";
    public IReadOnlyList<string>? ServerArgs { get; init; }
}

public sealed class VolumeMount
{
    public required string Host { get; init; }
    public required string Container { get; init; }
    public bool Readonly { get; init; }
}

public sealed class EnvVar
{
    public required string Key { get; init; }
    public required string Value { get; init; }
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

    /// <summary>
    /// Pulls a Docker image. Idempotent — no-op if image already local.
    /// Returns the image reference pulled. Throws on pull failure.
    /// </summary>
    Task<string> PullImageAsync(string image, CancellationToken ct = default);

    /// <summary>
    /// Creates a new Docker container from a config. Does NOT start it.
    /// Pre-checks: container name uniqueness.
    /// Returns ContainerId and MappedPort. Throws on create failure.
    /// </summary>
    Task<ContainerCreateResult> CreateContainerAsync(ContainerCreateConfig config, CancellationToken ct = default);
}
