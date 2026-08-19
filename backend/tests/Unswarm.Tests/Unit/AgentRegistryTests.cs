using System.Net.WebSockets;
using System.Text.Json;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class AgentRegistryTests : IDisposable
{
    private readonly AgentRegistry _registry = new();

    [Fact]
    public void Register_And_Get_ReturnsConnection()
    {
        var conn = MakeConnection("agent-1");
        var socket = new FakeWebSocket();

        _registry.Register("agent-1", conn, socket);

        var result = _registry.Get("agent-1");
        Assert.NotNull(result);
        Assert.Equal("agent-1", result!.Name);
        Assert.True(result.IsConnected);
    }

    [Fact]
    public void Get_Returns_Null_For_Unknown()
    {
        Assert.Null(_registry.Get("nonexistent"));
    }

    [Fact]
    public void List_Returns_All()
    {
        _registry.Register("a", MakeConnection("a"), new FakeWebSocket());
        _registry.Register("b", MakeConnection("b"), new FakeWebSocket());

        var list = _registry.List();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, c => c.Name == "a");
        Assert.Contains(list, c => c.Name == "b");
    }

    [Fact]
    public void Unregister_Removes_And_Sets_IsConnected_False()
    {
        var conn = MakeConnection("agent-1");
        _registry.Register("agent-1", conn, new FakeWebSocket());

        _registry.Unregister("agent-1", conn.ConnectionId);

        Assert.Null(_registry.Get("agent-1"));
        Assert.False(conn.IsConnected);
        Assert.Empty(_registry.List());
    }

    [Fact]
    public void Unregister_WrongConnectionId_DoesNotRemove()
    {
        var conn = MakeConnection("agent-1");
        _registry.Register("agent-1", conn, new FakeWebSocket());

        _registry.Unregister("agent-1", "wrong-connection-id");

        Assert.NotNull(_registry.Get("agent-1"));
        Assert.True(conn.IsConnected);
    }

    [Fact]
    public void Register_Overwrites_Existing_And_Closes_Old_Socket()
    {
        var oldSocket = new FakeWebSocket();
        var conn1 = MakeConnection("agent-1");
        _registry.Register("agent-1", conn1, oldSocket);

        var conn2 = MakeConnection("agent-1");
        var newSocket = new FakeWebSocket();
        _registry.Register("agent-1", conn2, newSocket);

        // New connection should be active
        var result = _registry.Get("agent-1");
        Assert.Same(conn2, result);

        // Old socket should have been closed
        Assert.Equal(1, oldSocket.CloseCallCount);

        // Old connection should be marked disconnected
        Assert.False(conn1.IsConnected);
    }

    [Fact]
    public async Task SendAsync_To_Registered_Agent_Sends_Message()
    {
        var socket = new FakeWebSocket();
        _registry.Register("agent-1", MakeConnection("agent-1"), socket);

        var msg = new AgentMessage
        {
            Type = "command",
            Id = "cmd-001",
            Payload = JsonSerializer.SerializeToElement(new { command = "start_container" })
        };

        var sent = await _registry.SendAsync("agent-1", msg);

        Assert.True(sent);
        Assert.Single(socket.SentMessages);
        var json = socket.SentMessages[0];
        Assert.Contains("\"type\":\"command\"", json);
        Assert.Contains("\"id\":\"cmd-001\"", json);
    }

    [Fact]
    public async Task SendAsync_To_Unknown_Agent_Returns_False()
    {
        var msg = new AgentMessage { Type = "heartbeat" };
        var sent = await _registry.SendAsync("nonexistent", msg);
        Assert.False(sent);
    }

    [Fact]
    public async Task SendAsync_To_Closed_Socket_Returns_False()
    {
        var socket = new FakeWebSocket();
        socket.Abort(); // sets state to Aborted

        _registry.Register("agent-1", MakeConnection("agent-1"), socket);

        var msg = new AgentMessage { Type = "heartbeat" };
        var sent = await _registry.SendAsync("agent-1", msg);

        Assert.False(sent);
    }

    [Fact]
    public void List_Empty_When_None()
    {
        Assert.Empty(_registry.List());
    }

    [Fact]
    public void GetInfo_ReturnsSnapshot_ForRegisteredAgent()
    {
        var socket = new FakeWebSocket();
        var conn = new AgentConnection
        {
            Name = "gpu1",
            ConnectionId = "cid-1",
            ConnectedAt = DateTimeOffset.UtcNow,
            LastSeen = DateTimeOffset.UtcNow,
            IsConnected = true,
            Version = "2.0",
            Hostname = "gpu-box",
            OsPlatform = "linux",
            GpuInfo = "NVIDIA RTX 3090 (8GB)",
            TotalMemoryMb = 16384,
            CpuCores = 8,
            Containers =
            [
                new AgentContainerStatus { ContainerId = "c1", ModelName = "llama", Status = "running", Port = 8080 }
            ]
        };
        _registry.Register("gpu1", conn, socket);

        var info = _registry.GetInfo("gpu1");

        Assert.NotNull(info);
        Assert.Equal("gpu1", info!.Name);
        Assert.Equal("cid-1", info.ConnectionId);
        Assert.True(info.IsConnected);
        Assert.Equal("2.0", info.Version);
        Assert.Equal("gpu-box", info.Hostname);
        Assert.Equal("linux", info.OsPlatform);
        Assert.Equal("NVIDIA RTX 3090 (8GB)", info.GpuInfo);
        Assert.Equal(16384, info.TotalMemoryMb);
        Assert.Equal(8, info.CpuCores);
        var container = Assert.Single(info.Containers);
        Assert.Equal("c1", container.ContainerId);
        Assert.Equal("llama", container.ModelName);
        Assert.Equal("running", container.Status);
        Assert.Equal(8080, container.Port);
    }

    [Fact]
    public void GetInfo_ReturnsNull_ForUnknown()
    {
        Assert.Null(_registry.GetInfo("nonexistent"));
    }

    [Fact]
    public void ListWithInfo_ReturnsAllRegistered()
    {
        _registry.Register("a", MakeConnection("a"), new FakeWebSocket());
        _registry.Register("b", MakeConnection("b"), new FakeWebSocket());

        var infos = _registry.ListWithInfo();

        Assert.Equal(2, infos.Count);
        Assert.Contains(infos, i => i.Name == "a" && i.IsConnected);
        Assert.Contains(infos, i => i.Name == "b" && i.IsConnected);
    }

    [Fact]
    public void ListWithInfo_Empty_WhenNone()
    {
        Assert.Empty(_registry.ListWithInfo());
    }

    [Fact]
    public void GetInfo_AfterUnregister_ReturnsNull()
    {
        var conn = MakeConnection("gpu1");
        _registry.Register("gpu1", conn, new FakeWebSocket());

        _registry.Unregister("gpu1", conn.ConnectionId);

        Assert.Null(_registry.GetInfo("gpu1"));
        Assert.Empty(_registry.ListWithInfo());
    }

    private static AgentConnection MakeConnection(string name) => new()
    {
        Name = name,
        ConnectionId = Guid.NewGuid().ToString("N"),
        ConnectedAt = DateTimeOffset.UtcNow,
        LastSeen = DateTimeOffset.UtcNow,
        IsConnected = true
    };

    public void Dispose()
    {
        // cleanup if needed
    }
}
