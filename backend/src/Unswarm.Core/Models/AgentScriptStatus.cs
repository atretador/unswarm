namespace Unswarm.Core.Models;

/// <summary>
/// Runtime status of a launcher script process on a remote agent.
/// Populated from the agent's telemetry "scripts" field.
/// </summary>
public sealed class AgentScriptStatus
{
    public required string Path { get; init; }
    public int PID { get; init; }
    public required string Status { get; init; } // "running" | "stopped"
    public int Port { get; init; }
    public long StartTime { get; init; } // unix ms
}
