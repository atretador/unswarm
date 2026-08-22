using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Core.Services;

public sealed class AgentRegistry : IAgentRegistry
{
    private readonly ConcurrentDictionary<string, AgentRegistryEntry> _agents = new(StringComparer.Ordinal);

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
            old.SendLock.Dispose();
            _ = TryCloseSocketAsync(old.Socket);
        }

        _agents[name] = new AgentRegistryEntry(connection, socket);
    }

    // M1: connectionId-based unregister — only remove if the stored connection matches
    public void Unregister(string name, string connectionId)
    {
        if (_agents.TryGetValue(name, out var entry) && entry.Connection.ConnectionId == connectionId)
        {
            entry.Connection.IsConnected = false;
            _agents.TryRemove(name, out _);
            entry.SendLock.Dispose();
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

    /// <summary>
    /// Fail all pending commands for a disconnected agent. Called from the
    /// WebSocket disconnect path so callers don't hang for 60-120s.
    /// </summary>
    public void NotifyAgentDisconnected(string name)
    {
        // This is handled by RemoteAgentDockerController.FailPendingCommands
        // via the DockerControllerRouter — no-op here; the registry just tracks connections.
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
        if (!_agents.TryGetValue(name, out var entry))
            return false;

        var socket = entry.Socket;
        if (socket.State != WebSocketState.Open)
            return false;

        await entry.SendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check state after acquiring the lock — socket may have closed while waiting
            if (socket.State != WebSocketState.Open)
                return false;

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
        finally
        {
            entry.SendLock.Release();
        }
    }

    private sealed class AgentRegistryEntry : IDisposable
    {
        public AgentConnection Connection { get; }
        public WebSocket Socket { get; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);

        public AgentRegistryEntry(AgentConnection connection, WebSocket socket)
        {
            Connection = connection;
            Socket = socket;
        }

        public void Dispose()
        {
            SendLock.Dispose();
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
