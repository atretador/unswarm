namespace Unswarm.Core.Models;

public sealed class ContainerInfo
{
    public required string Id { get; init; }
    public required string ModelId { get; init; }
    public required string ModelName { get; init; }
    public ContainerStatus Status { get; init; }
    public int? Port { get; init; }
    public int? Pid { get; init; }
    public long MemoryMb { get; init; }
    public double CpuPercent { get; init; }
    public long Uptime { get; init; }
    public DateTimeOffset? LastHealthCheck { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? RegisteredRuntimeId { get; init; }
}
