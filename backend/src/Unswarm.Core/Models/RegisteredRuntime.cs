namespace Unswarm.Core.Models;

public enum ContainerRegistrationStatus
{
    Registered,
    Starting,
    Healthy,
    Discovering,
    Ready,
    Error
}

public sealed record RegisteredRuntime
{
    public required string Id { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    /// <summary>Container name to interact with (pre-provisioned container).</summary>
    public required string Image { get; init; }
    public int ContainerPort { get; init; } = 8080;
    /// <summary>Discriminator: Container (default) or Script.</summary>
    public RuntimeKind RuntimeKind { get; init; } = RuntimeKind.Container;
    /// <summary>Filesystem path to a host script (only set when RuntimeKind = Script).</summary>
    public string? LauncherPath { get; init; }
    /// <summary>Process id when a Script runtime is running (null for Container runtimes).</summary>
    public int? RuntimeProcessId { get; init; }
    public string? GpuDevices { get; init; }
    public long MemoryLimitMb { get; init; }
    public Dictionary<string, string> ExtraLabels { get; init; } = [];
    /// <summary>Execution target agent name; "host" for local Docker.</summary>
    public string Agent { get; init; } = "host";
    /// <summary>
    /// Same-agent container names this container may run concurrently with.
    /// A container may start only if every currently-running container on its agent is in
    /// this set AND this container is in each running container's set (symmetric).
    /// Empty = single-container mode (must run alone on its agent).
    /// </summary>
    public IReadOnlyList<string> CanRunAlongWith { get; init; } = [];
    public ContainerRegistrationStatus Status { get; init; } = ContainerRegistrationStatus.Registered;
    public string? RuntimeContainerId { get; init; }
    public int? MappedPort { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? LastDiscoveredAt { get; init; }
    public int MaxConcurrentInferences { get; init; } = 1;
}
