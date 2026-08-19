using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Core.Services;

public sealed class AgentRegistry : IAgentRegistry
{
    private readonly ConcurrentDictionary<string, (AgentConnection Connection, WebSocket Socket)> _agents = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public void Register(string name, AgentConnection connection, WebSocket socket)
    {
        // M1: If overwriting an existing connection, close the old socket so its ReadLoop terminates
        if (_agents.TryRemove(name, out var old))
        {
            old.Connection.IsConnected = false;
            _ = TryCloseSocketAsync(old.Socket);
        }

        _agents[name] = (connection, socket);
    }

    // M1: connectionId-based unregister — only remove if the stored connection matches
    public void Unregister(string name, string connectionId)
    {
        if (_agents.TryGetValue(name, out var entry) && entry.Connection.ConnectionId == connectionId)
        {
            entry.Connection.IsConnected = false;
            _agents.TryRemove(name, out _);
        }
    }

    public AgentConnection? Get(string name)
    {
        return _agents.TryGetValue(name, out var entry) ? entry.Connection : null;
    }

    public IReadOnlyList<AgentConnection> List()
    {
        return _agents.Values.Select(e => e.Connection).ToList();
    }

    public AgentInfo? GetInfo(string name)
    {
        var connection = Get(name);
        return connection is null ? null : ToInfo(connection);
    }

    public IReadOnlyList<AgentInfo> ListWithInfo()
    {
        return _agents.Values
            .Select(e => ToInfo(e.Connection))
            .ToList();
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
        Containers = connection.Containers
    };

    public async Task<bool> SendAsync(string name, AgentMessage message, CancellationToken ct = default)
    {
        if (!_agents.TryGetValue(name, out var entry))
            return false;

        var socket = entry.Socket;
        if (socket.State != WebSocketState.Open)
            return false;

        try
        {
            var json = JsonSerializer.Serialize(message, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task TryCloseSocketAsync(WebSocket socket)
    {
        try
        {
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None);
            }
        }
        catch
        {
            // Best effort — old ReadLoop will break on next ReceiveAsync
        }
    }
}
