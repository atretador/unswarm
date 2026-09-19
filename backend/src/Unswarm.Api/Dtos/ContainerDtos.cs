using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Api.Dtos;

public sealed class ContainerResponse
{
    public string Id { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string ModelDisplayName { get; set; } = "";
    public ContainerStatus Status { get; set; }
    public int? Port { get; set; }
    public int? Pid { get; set; }
    public long MemoryMb { get; set; }
    public double CpuPercent { get; set; }
    public long Uptime { get; set; }
    public DateTimeOffset? LastHealthCheck { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public static ContainerResponse FromContainerInfo(ContainerInfo c) => new()
    {
        Id = c.Id,
        ModelId = c.ModelId,
        ModelName = c.ModelName,
        Status = c.Status,
        Port = c.Port,
        Pid = c.Pid,
        MemoryMb = c.MemoryMb,
        CpuPercent = c.CpuPercent,
        Uptime = c.Uptime,
        LastHealthCheck = c.LastHealthCheck,
        ErrorMessage = c.ErrorMessage,
        CreatedAt = c.CreatedAt
    };
}

public sealed class ContainerStartRequest
{
    public string ModelId { get; set; } = "";
}

/// <summary>
/// DTO for creating a new container registration.
/// </summary>
public sealed class RegisterRuntimeRequestDto
{
    public string DisplayName { get; set; } = "";
    public required string Image { get; set; }
    public int ContainerPort { get; set; } = 8080;
    /// <summary>Host-mapped port (resolved from Docker inspect). Null = auto-resolve on registration.</summary>
    public int? MappedPort { get; set; }
    public string? RuntimeKind { get; set; }
    public string? LauncherPath { get; set; }
    public string Agent { get; set; } = "host";
    public List<string>? CanRunAlongWith { get; set; }
    public Dictionary<string, string>? ExtraLabels { get; set; }
    public int MaxConcurrentInferences { get; set; } = 1;

    public ContainerRegistrationRequest ToRequest() => new()
    {
        DisplayName = DisplayName,
        Image = Image,
        RuntimeKind = RuntimeKind?.ToLowerInvariant() == "script" ? Unswarm.Core.Models.RuntimeKind.Script : Unswarm.Core.Models.RuntimeKind.Container,
        LauncherPath = LauncherPath,
        ContainerPort = ContainerPort,
        MappedPort = MappedPort,
        Agent = Agent,
        CanRunAlongWith = CanRunAlongWith ?? [],
        ExtraLabels = ExtraLabels ?? []
    };
}

/// <summary>
/// DTO for updating a registered runtime's display name, port, and concurrency settings.
/// </summary>
public sealed class UpdateRuntimeRequestDto
{
    public string? DisplayName { get; set; }
    public int? ContainerPort { get; set; }
    public int? MappedPort { get; set; }
    public int? MaxConcurrentInferences { get; set; }
}

/// <summary>
/// DTO for updating a registered runtime's concurrency allow-list.
/// </summary>
public sealed class UpdateRuntimeConcurrencyRequestDto
{
    public List<string>? CanRunAlongWith { get; set; }
    public int? MaxConcurrentInferences { get; set; }
}

/// <summary>
/// DTO for atomically toggling concurrency between two runtimes.
/// </summary>
public sealed class ToggleConcurrencyRequestDto
{
    public string RuntimeAId { get; set; } = "";
    public string RuntimeBId { get; set; } = "";
    public bool CanRunAlongWith { get; set; }
}

/// <summary>
/// DTO for returning a registered container and its discovered models.
/// </summary>
public sealed class RegisteredRuntimeResponse
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Image { get; set; } = "";
    public int ContainerPort { get; set; }
    public string Agent { get; set; } = "host";
    public string RuntimeKind { get; set; } = "container";
    public string? LauncherPath { get; set; }
    public List<string> CanRunAlongWith { get; set; } = [];
    public string Status { get; set; } = "";
    public string? RuntimeContainerId { get; set; }
    public int? RuntimeProcessId { get; set; }
    public int? MappedPort { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastDiscoveredAt { get; set; }
    public int MaxConcurrentInferences { get; set; } = 1;
    public string CreationMode { get; set; } = "preProvisioned";
    public string? ErrorDetail { get; set; }
    public string? ErrorLogs { get; set; }
    public List<ModelResponse> DiscoveredModels { get; set; } = [];

    public static RegisteredRuntimeResponse From(
        RegisteredRuntime container,
        IReadOnlyList<ModelDefinition> discoveredModels) => new()
    {
        Id = container.Id,
        DisplayName = container.DisplayName,
        Image = container.Image,
        ContainerPort = container.ContainerPort,
        Agent = container.Agent,
        RuntimeKind = container.RuntimeKind.ToString().ToLowerInvariant(),
        LauncherPath = container.LauncherPath,
        CanRunAlongWith = (container.CanRunAlongWith ?? []).ToList(),
        Status = container.Status.ToString().ToLowerInvariant(),
        RuntimeContainerId = container.RuntimeContainerId,
        RuntimeProcessId = container.RuntimeProcessId,
        MappedPort = container.MappedPort,
        ErrorMessage = container.ErrorMessage,
        CreatedAt = container.CreatedAt,
        LastDiscoveredAt = container.LastDiscoveredAt,
        MaxConcurrentInferences = container.MaxConcurrentInferences,
        CreationMode = container.CreationMode.ToString().ToLowerInvariant(),
        ErrorDetail = container.ErrorDetail,
        ErrorLogs = container.ErrorLogs,
        DiscoveredModels = discoveredModels.Select(ModelResponse.FromDefinition).ToList()
    };
}

/// <summary>
/// DTO for creating a new container from a Docker image.
/// </summary>
public sealed class CreateContainerRequestDto
{
    public required string Image { get; set; }
    public string Name { get; set; } = "";
    public DockerCreateParamsDto? DockerParams { get; set; }
    public string Agent { get; set; } = "host";
    public bool Detach { get; set; }
}

public sealed class DockerCreateParamsDto
{
    public string? ContainerName { get; set; }
    public int ContainerPort { get; set; } = 8080;
    public int? HostPort { get; set; }
    public List<string>? Devices { get; set; }
    public List<VolumeMountDto>? Volumes { get; set; }
    public List<EnvVarDto>? Env { get; set; }
    public int ShmSizeMb { get; set; } = 16384;
    public string IpcMode { get; set; } = "private";
    public string NetworkMode { get; set; } = "bridge";
    public string RestartPolicy { get; set; } = "unless-stopped";
    public List<string>? ServerArgs { get; set; }
}

public sealed class VolumeMountDto
{
    public string Host { get; set; } = "";
    public string Container { get; set; } = "";
    public bool Readonly { get; set; }
}

public sealed class EnvVarDto
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// Response DTO for container creation requests.
/// </summary>
public sealed class CreateContainerResponseDto
{
    public string RuntimeId { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Message { get; set; }
    public string? ContainerId { get; set; }
    public string? ErrorDetail { get; set; }
}
