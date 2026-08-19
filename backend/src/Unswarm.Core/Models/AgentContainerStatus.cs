namespace Unswarm.Core.Models;

public sealed class AgentContainerStatus
{
    public required string ContainerId { get; init; }
    public string? ModelName { get; init; }
    public string Status { get; init; } = ""; // "running", "exited", etc.
    public int? Port { get; init; }
}
