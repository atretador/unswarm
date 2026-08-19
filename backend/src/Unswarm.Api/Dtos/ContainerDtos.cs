using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Api.Dtos;

public sealed class ContainerResponse
{
    public string Id { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string ModelName { get; set; } = "";
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
public sealed class ContainerRegistrationRequestDto
{
    public string DisplayName { get; set; } = "";
    public required string Image { get; set; }
    public int ContainerPort { get; set; } = 8080;
    public string Agent { get; set; } = "host";
    public List<string>? CanRunAlongWith { get; set; }
    public Dictionary<string, string>? ExtraLabels { get; set; }

    public ContainerRegistrationRequest ToRequest() => new()
    {
        DisplayName = DisplayName,
        Image = Image,
        ContainerPort = ContainerPort,
        Agent = Agent,
        CanRunAlongWith = CanRunAlongWith ?? [],
        ExtraLabels = ExtraLabels ?? []
    };
}

/// <summary>
/// DTO for returning a registered container and its discovered models.
/// </summary>
public sealed class RegisteredContainerResponse
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Image { get; set; } = "";
    public int ContainerPort { get; set; }
    public string Agent { get; set; } = "host";
    public List<string> CanRunAlongWith { get; set; } = [];
    public string Status { get; set; } = "";
    public string? RuntimeContainerId { get; set; }
    public int? MappedPort { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastDiscoveredAt { get; set; }
    public List<ModelResponse> DiscoveredModels { get; set; } = [];

    public static RegisteredContainerResponse From(
        RegisteredContainer container,
        IReadOnlyList<ModelDefinition> discoveredModels) => new()
    {
        Id = container.Id,
        DisplayName = container.DisplayName,
        Image = container.Image,
        ContainerPort = container.ContainerPort,
        Agent = container.Agent,
        CanRunAlongWith = (container.CanRunAlongWith ?? []).ToList(),
        Status = container.Status.ToString(),
        RuntimeContainerId = container.RuntimeContainerId,
        MappedPort = container.MappedPort,
        ErrorMessage = container.ErrorMessage,
        CreatedAt = container.CreatedAt,
        LastDiscoveredAt = container.LastDiscoveredAt,
        DiscoveredModels = discoveredModels.Select(ModelResponse.FromDefinition).ToList()
    };
}
