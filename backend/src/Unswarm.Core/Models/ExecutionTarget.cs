namespace Unswarm.Core.Models;

/// <summary>
/// Where a model's container executes. "host" is local Docker; "agent:&lt;name&gt;"
/// is a remote agent (by AgentConnection.Name). Containers on different targets
/// run concurrently; single-slot stop/start behavior applies only within a target.
/// </summary>
public enum ExecutionTargetKind
{
    Host,
    Agent
}

public sealed record ExecutionTarget
{
    public const string HostId = "host";
    private const string AgentPrefix = "agent:";

    /// <summary>"host" or "agent:&lt;name&gt;".</summary>
    public required string Id { get; init; }
    public ExecutionTargetKind Kind { get; init; }
    public string? AgentName { get; init; }

    public bool IsAgent => Kind == ExecutionTargetKind.Agent;

    public static ExecutionTarget Host => new() { Id = HostId, Kind = ExecutionTargetKind.Host };

    public static ExecutionTarget ForAgent(string name) => new()
    {
        Id = $"{AgentPrefix}{name}",
        Kind = ExecutionTargetKind.Agent,
        AgentName = name
    };

    /// <summary>Parses "host" or "agent:&lt;name&gt;". Anything else defaults to host.</summary>
    public static ExecutionTarget FromId(string id)
    {
        if (!string.IsNullOrEmpty(id) && id.StartsWith(AgentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = id[AgentPrefix.Length..];
            if (!string.IsNullOrWhiteSpace(name))
                return ForAgent(name);
        }

        return Host;
    }
}
