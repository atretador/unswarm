using System.Net.WebSockets;
using Unswarm.Core.Models;

namespace Unswarm.Core.Contracts;

public interface IAgentRegistry
{
    void Register(string name, AgentConnection connection, WebSocket socket);
    void Unregister(string name, string connectionId);
    AgentConnection? Get(string name);
    IReadOnlyList<AgentConnection> List();
    Task<bool> SendAsync(string name, AgentMessage message, CancellationToken ct = default);

    /// <summary>Snapshot of a single agent's connection + enriched telemetry.</summary>
    AgentInfo? GetInfo(string name);

    /// <summary>Snapshots of all registered agents (connected or not).</summary>
    IReadOnlyList<AgentInfo> ListWithInfo();
}
