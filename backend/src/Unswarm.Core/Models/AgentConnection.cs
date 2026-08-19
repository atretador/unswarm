namespace Unswarm.Core.Models;

public sealed class AgentConnection
{
    public required string Name { get; init; }
    public required string ConnectionId { get; init; }
    public DateTimeOffset ConnectedAt { get; init; }
    public DateTimeOffset LastSeen { get; set; }
    public bool IsConnected { get; set; }
    public string? DockerSocket { get; init; }
    public string? Version { get; init; }
    public string? Hostname { get; set; }
    public string? OsPlatform { get; set; }
    public string? GpuInfo { get; set; }
    public long TotalMemoryMb { get; set; }
    public int CpuCores { get; set; }
    public IReadOnlyList<AgentContainerStatus> Containers { get; set; } = [];
}
