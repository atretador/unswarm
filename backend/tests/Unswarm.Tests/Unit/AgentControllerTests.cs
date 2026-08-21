using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Unswarm.Api.Controllers;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class AgentControllerTests : IDisposable
{
    private readonly AgentRegistry _registry = new();
    private readonly AgentController _controller;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public AgentControllerTests()
    {
        _controller = new AgentController(_registry, NullLogger<AgentController>.Instance);
    }

    [Fact]
    public async Task ValidCamelCaseHello_RegistersAgent()
    {
        var socket = new FakeWebSocket();

        // Enqueue hello message (camelCase) only — no close yet, so the agent
        // stays registered while we assert. HandleConnectionAsync runs in the
        // background and blocks in the read loop until we enqueue a close.
        var helloJson = JsonSerializer.Serialize(new
        {
            type = "hello",
            payload = new { name = "test-agent", dockerSocket = "/var/run/docker.sock", version = "1.0" }
        }, JsonOptions);
        socket.EnqueueReceive(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(helloJson));

        var task = _controller.HandleConnectionAsync(socket, CancellationToken.None);

        // Poll the registry while the connection is alive
        var agent = await WaitForAgentAsync("test-agent");
        Assert.NotNull(agent);
        Assert.Equal("test-agent", agent!.Name);
        Assert.Equal("/var/run/docker.sock", agent.DockerSocket);
        Assert.Equal("1.0", agent.Version);
        Assert.True(agent.IsConnected);

        // Should have received hello ack
        Assert.Contains(socket.SentMessages, m => m.Contains("\"type\":\"hello\""));

        // Close to let the connection finish and unregister
        socket.EnqueueReceive(WebSocketMessageType.Close, []);
        await task;
    }

    private async Task<AgentConnection?> WaitForAgentAsync(string name, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var agent = _registry.Get(name);
            if (agent is not null)
                return agent;
            await Task.Delay(10);
        }
        return null;
    }

    [Fact]
    public async Task NonHelloFirstMessage_ReturnsErrorAndDisconnects()
    {
        var socket = new FakeWebSocket();

        var msg = JsonSerializer.Serialize(new { type = "heartbeat" }, JsonOptions);
        socket.EnqueueReceive(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(msg));

        await _controller.HandleConnectionAsync(socket, CancellationToken.None);

        // Debug: dump all sent messages
        Console.WriteLine($"SENT_COUNT: {socket.SentMessages.Count}");
        for (int i = 0; i < socket.SentMessages.Count; i++)
            Console.WriteLine($"SENT[{i}] (len={socket.SentMessages[i].Length}): {socket.SentMessages[i].Substring(0, Math.Min(200, socket.SentMessages[i].Length))}");

        // Should have received error message
        Assert.Single(socket.SentMessages);
        Assert.Contains("error", socket.SentMessages[0]);

        // Agent should NOT be registered
        Assert.Null(_registry.Get("any-agent"));
    }

    [Fact]
    public async Task HeartbeatIsAcked()
    {
        var socket = new FakeWebSocket();

        // Enqueue hello
        var helloJson = JsonSerializer.Serialize(new
        {
            type = "hello",
            payload = new { name = "hb-agent" }
        }, JsonOptions);
        socket.EnqueueReceive(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(helloJson));

        // Enqueue heartbeat
        var hbJson = JsonSerializer.Serialize(new { type = "heartbeat", id = "hb-1" }, JsonOptions);
        socket.EnqueueReceive(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(hbJson));

        // Enqueue close
        socket.EnqueueReceive(WebSocketMessageType.Close, []);

        await _controller.HandleConnectionAsync(socket, CancellationToken.None);

        // Should have received hello ack + heartbeat ack
        Assert.Contains(socket.SentMessages, m => m.Contains("\"type\":\"heartbeat\""));
        Assert.Contains(socket.SentMessages, m => m.Contains("\"id\":\"hb-1\""));
    }

    [Fact]
    public async Task DisconnectTriggersUnregister()
    {
        var socket = new FakeWebSocket();

        var helloJson = JsonSerializer.Serialize(new
        {
            type = "hello",
            payload = new { name = "disconnect-agent" }
        }, JsonOptions);
        socket.EnqueueReceive(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(helloJson));

        // Enqueue close to trigger disconnect
        socket.EnqueueReceive(WebSocketMessageType.Close, []);

        await _controller.HandleConnectionAsync(socket, CancellationToken.None);

        // Agent should be unregistered
        Assert.Null(_registry.Get("disconnect-agent"));
    }

    [Fact]
    public async Task HelloWithoutName_ReturnsError()
    {
        var socket = new FakeWebSocket();

        var helloJson = JsonSerializer.Serialize(new
        {
            type = "hello",
            payload = new { } // missing name
        }, JsonOptions);
        socket.EnqueueReceive(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(helloJson));

        await _controller.HandleConnectionAsync(socket, CancellationToken.None);

        Assert.Contains(socket.SentMessages, m => m.Contains("hello payload must include: name"));
        Assert.Null(_registry.Get("any-agent"));
    }

    [Fact]
    public async Task HelloWithEmptyName_ReturnsError()
    {
        var socket = new FakeWebSocket();

        var helloJson = JsonSerializer.Serialize(new
        {
            type = "hello",
            payload = new { name = "" }
        }, JsonOptions);
        socket.EnqueueReceive(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(helloJson));

        await _controller.HandleConnectionAsync(socket, CancellationToken.None);

        Assert.Contains(socket.SentMessages, m => m.Contains("name cannot be empty"));
    }

    [Fact]
    public async Task UnknownMessageType_ReturnsError()
    {
        var socket = new FakeWebSocket();

        var helloJson = JsonSerializer.Serialize(new
        {
            type = "hello",
            payload = new { name = "error-test" }
        }, JsonOptions);
        socket.EnqueueReceive(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(helloJson));

        var unknownJson = JsonSerializer.Serialize(new { type = "bogus" }, JsonOptions);
        socket.EnqueueReceive(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(unknownJson));

        socket.EnqueueReceive(WebSocketMessageType.Close, []);

        await _controller.HandleConnectionAsync(socket, CancellationToken.None);

        // Should get error for unknown type
        Assert.Contains(socket.SentMessages, m => m.Contains("Unknown message type: bogus"));
    }

    [Fact]
    public async Task TelemetryMessage_UpdatesAgentConnection()
    {
        var socket = new FakeWebSocket();

        var helloJson = JsonSerializer.Serialize(new
        {
            type = "hello",
            payload = new { name = "telemetry-agent" }
        }, JsonOptions);
        socket.EnqueueReceive(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(helloJson));

        var telemetryJson = JsonSerializer.Serialize(new
        {
            type = "telemetry",
            payload = new
            {
                hostname = "gpu-box-1",
                osPlatform = "linux",
                gpuInfo = "NVIDIA RTX 3090 (8GB), NVIDIA A100 (16GB)",
                totalMemoryMb = 32768,
                cpuCores = 16,
                containers = new object[]
                {
                    new { id = "abc123", name = "my-model", status = "running", port = 8080 },
                    new { id = "def456", name = "other-model", status = "exited", port = (int?)null }
                },
                scripts = new object[]
                {
                    new { path = "/opt/scripts/model-a.sh", pid = 5678, status = "running", port = 9000, startTime = (long)1700000000000 }
                }
            }
        }, JsonOptions);
        socket.EnqueueReceive(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(telemetryJson));

        // No close yet — agent stays registered while we assert, then close to finish.
        var task = _controller.HandleConnectionAsync(socket, CancellationToken.None);

        var agent = await WaitForAgentAsync("telemetry-agent");
        Assert.NotNull(agent);

        // Poll until telemetry is parsed (read loop is async)
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && agent!.Hostname is null)
            await Task.Delay(10);

        Assert.Equal("gpu-box-1", agent!.Hostname);
        Assert.Equal("linux", agent.OsPlatform);
        Assert.Equal("NVIDIA RTX 3090 (8GB), NVIDIA A100 (16GB)", agent.GpuInfo);
        Assert.Equal(32768, agent.TotalMemoryMb);
        Assert.Equal(16, agent.CpuCores);

        Assert.Equal(2, agent.Containers.Count);
        Assert.Equal("abc123", agent.Containers[0].ContainerId);
        Assert.Equal("my-model", agent.Containers[0].ModelName);
        Assert.Equal("running", agent.Containers[0].Status);
        Assert.Equal(8080, agent.Containers[0].Port);
        Assert.Equal("exited", agent.Containers[1].Status);
        Assert.Null(agent.Containers[1].Port);

        // Phase 3: scripts parsed from telemetry
        Assert.Single(agent.Scripts);
        Assert.Equal("/opt/scripts/model-a.sh", agent.Scripts[0].Path);
        Assert.Equal(5678, agent.Scripts[0].PID);
        Assert.Equal("running", agent.Scripts[0].Status);
        Assert.Equal(9000, agent.Scripts[0].Port);
        Assert.Equal(1700000000000, agent.Scripts[0].StartTime);

        socket.EnqueueReceive(WebSocketMessageType.Close, []);
        await task;
    }

    [Fact]
    public async Task TelemetryMessage_WithoutPayload_DoesNotThrow()
    {
        var socket = new FakeWebSocket();

        var helloJson = JsonSerializer.Serialize(new
        {
            type = "hello",
            payload = new { name = "no-payload-agent" }
        }, JsonOptions);
        socket.EnqueueReceive(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(helloJson));

        socket.EnqueueReceive(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("{\"type\":\"telemetry\"}"));
        socket.EnqueueReceive(WebSocketMessageType.Close, []);

        await _controller.HandleConnectionAsync(socket, CancellationToken.None);

        // Should not throw; agent unregistered after close
        Assert.Null(_registry.Get("no-payload-agent"));
    }

    [Fact]
    public async Task CommandResult_IsRoutedToRouter()
    {
        var socket = new FakeWebSocket();
        AgentMessage? routed = null;
        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = new FakeDockerController() });
        router.OnIncoming = (name, msg) => routed = msg;
        var controller = new AgentController(_registry, NullLogger<AgentController>.Instance, router);

        var helloJson = JsonSerializer.Serialize(new
        {
            type = "hello",
            payload = new { name = "router-agent" }
        }, JsonOptions);
        socket.EnqueueReceive(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(helloJson));

        var resultJson = JsonSerializer.Serialize(new
        {
            type = "command_result",
            id = "cmd-001",
            payload = new { containerId = "abc123" }
        }, JsonOptions);
        socket.EnqueueReceive(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(resultJson));

        socket.EnqueueReceive(WebSocketMessageType.Close, []);

        await controller.HandleConnectionAsync(socket, CancellationToken.None);

        Assert.NotNull(routed);
        Assert.Equal("cmd-001", routed!.Id);
        Assert.Equal("command_result", routed.Type);
    }

    public void Dispose()
    {
        // cleanup
    }
}
