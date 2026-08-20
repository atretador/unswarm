using System.Collections.Concurrent;
using System.Net.WebSockets;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// In-memory IAgentRegistry for testing remote controllers. SendAsync records the
/// message and may auto-deliver a reply via OnSend; when Connected is false sends fail.
/// </summary>
public sealed class FakeAgentRegistry : IAgentRegistry
{
    public bool Connected { get; set; } = true;
    public List<string> RegisteredNames { get; } = [];

    public ConcurrentQueue<AgentMessage> SentMessages { get; } = new();

    /// <summary>Called on every SendAsync. Return non-null to deliver a command_result automatically.</summary>
    public Func<AgentMessage, Task<AgentMessage?>>? OnSend { get; set; }

    public void Register(string name, AgentConnection connection, WebSocket socket)
    {
        RegisteredNames.Add(name);
    }

    public void Unregister(string name, string connectionId)
    {
        RegisteredNames.Remove(name);
    }

    public AgentConnection? Get(string name)
    {
        if (!Connected || !RegisteredNames.Contains(name))
            return null;

        return new AgentConnection
        {
            Name = name,
            ConnectionId = "fake-cid",
            ConnectedAt = DateTimeOffset.UtcNow,
            LastSeen = DateTimeOffset.UtcNow,
            IsConnected = true,
            Version = "test"
        };
    }

    public IReadOnlyList<AgentConnection> List()
    {
        if (!Connected)
            return [];

        return RegisteredNames.Select(name => new AgentConnection
        {
            Name = name,
            ConnectionId = "fake-cid",
            ConnectedAt = DateTimeOffset.UtcNow,
            LastSeen = DateTimeOffset.UtcNow,
            IsConnected = true,
            Version = "test"
        }).ToList();
    }

    public AgentInfo? GetInfo(string name)
    {
        var connection = Get(name);
        return connection is null ? null : ToInfo(connection);
    }

    public IReadOnlyList<AgentInfo> ListWithInfo()
    {
        if (!Connected)
            return [];

        return RegisteredNames.Select(name => ToInfo(new AgentConnection
        {
            Name = name,
            ConnectionId = "fake-cid",
            ConnectedAt = DateTimeOffset.UtcNow,
            LastSeen = DateTimeOffset.UtcNow,
            IsConnected = true,
            Version = "test"
        })).ToList();
    }

    private static AgentInfo ToInfo(AgentConnection connection) => new()
    {
        Name = connection.Name,
        ConnectionId = connection.ConnectionId,
        ConnectedAt = connection.ConnectedAt,
        LastSeen = connection.LastSeen,
        IsConnected = connection.IsConnected,
        DockerSocket = connection.DockerSocket,
        Version = connection.Version,
        Hostname = connection.Hostname,
        OsPlatform = connection.OsPlatform,
        GpuInfo = connection.GpuInfo,
        TotalMemoryMb = connection.TotalMemoryMb,
        CpuCores = connection.CpuCores,
        Containers = connection.Containers,
        Scripts = connection.Scripts
    };

    public async Task<bool> SendAsync(string name, AgentMessage message, CancellationToken ct = default)
    {
        if (!Connected)
            return false;

        SentMessages.Enqueue(message);

        if (OnSend is not null)
        {
            var reply = await OnSend(message).ConfigureAwait(false);
            if (reply is not null)
            {
                // Deliver directly — the caller wires HandleIncomingMessage; here the
                // fake just returns the reply for the test to route.
            }
        }

        return true;
    }
}
