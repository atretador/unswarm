namespace Unswarm.Core.Models;

public sealed class AgentInfo
{
    public required string Name { get; init; }
    public string? ConnectionId { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public DateTimeOffset? LastSeen { get; init; }
    public bool IsConnected { get; init; }
    public string? DockerSocket { get; init; }
    public string? Version { get; init; }
    public string? Hostname { get; init; }
    public string? OsPlatform { get; init; }
    public string? GpuInfo { get; init; }
    public long TotalMemoryMb { get; init; }
    public int CpuCores { get; init; }
    public IReadOnlyList<AgentContainerStatus> Containers { get; init; } = [];
    public IReadOnlyList<AgentScriptStatus> Scripts { get; init; } = [];
}
