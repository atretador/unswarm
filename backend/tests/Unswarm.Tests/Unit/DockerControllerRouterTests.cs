using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using Unswarm.Core.Services.Remote;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class DockerControllerRouterTests
{
    private readonly FakeDockerController _hostController = new() { IdPrefix = "host" };
    private readonly AgentRegistry _registry = new();

    private DockerControllerRouter CreateRouter() => new(_hostController, _registry);

    private static AgentConnection MakeConnection(string name) => new()
    {
        Name = name,
        ConnectionId = Guid.NewGuid().ToString("N"),
        ConnectedAt = DateTimeOffset.UtcNow,
        LastSeen = DateTimeOffset.UtcNow,
        IsConnected = true
    };

    private static AgentMessage MakeCommandResult(string commandId, string containerId)
        => new()
        {
            Type = "command_result",
            Id = commandId,
            Agent = "gpu1",
            Payload = JsonSerializer.SerializeToElement(new { containerId })
        };

    /// <summary>Extracts the "id" of the command message sent to the given socket.</summary>
    private static string GetSentCommandId(FakeWebSocket socket)
    {
        var commandJson = socket.SentMessages.First(m => m.Contains("\"type\":\"command\""));
        using var doc = JsonDocument.Parse(commandJson);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    private static RemoteAgentDockerController AsRemote(IDockerController controller)
        => Assert.IsType<RemoteAgentDockerController>(controller);

    [Fact]
    public async Task HandleIncomingMessage_RoutesToCorrectController()
    {
        var socket = new FakeWebSocket();
        _registry.Register("gpu1", MakeConnection("gpu1"), socket);
        var router = CreateRouter();
        var controller = router.GetController("agent:gpu1");

        var startTask = controller.StartContainerAsync("vllm-1");

        // Wait for the command to be sent over the wire
        var deadline = DateTime.UtcNow.AddMilliseconds(2000);
        while (DateTime.UtcNow < deadline && socket.SentMessages.Count == 0)
            await Task.Delay(10);

        Assert.Equal(1, AsRemote(controller).PendingCommandCount);

        // Route a matching command_result through the router (not the controller directly)
        var commandId = GetSentCommandId(socket);
        router.HandleIncomingMessage("gpu1", MakeCommandResult(commandId, "abc123"));

        var result = await startTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("abc123", result.ContainerId);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(0, AsRemote(controller).PendingCommandCount);
    }

    [Fact]
    public async Task HandleIncomingMessage_DoesNotCrossAgents()
    {
        var socket = new FakeWebSocket();
        _registry.Register("gpu1", MakeConnection("gpu1"), socket);
        var router = CreateRouter();
        var controller = router.GetController("agent:gpu1");

        var startTask = controller.StartContainerAsync("vllm-1");

        var deadline = DateTime.UtcNow.AddMilliseconds(2000);
        while (DateTime.UtcNow < deadline && socket.SentMessages.Count == 0)
            await Task.Delay(10);

        var commandId = GetSentCommandId(socket);

        // A result for a DIFFERENT agent (or with wrong name) must not resolve the pending command
        router.HandleIncomingMessage("other-agent", MakeCommandResult(commandId, "wrong"));

        Assert.Equal(1, AsRemote(controller).PendingCommandCount);

        // Correct agent resolves it
        router.HandleIncomingMessage("gpu1", MakeCommandResult(commandId, "abc123"));
        var result = await startTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("abc123", result.ContainerId);
        Assert.Equal(0, AsRemote(controller).PendingCommandCount);
    }

    [Fact]
    public void HandleIncomingMessage_NoController_DoesNotThrow()
    {
        var router = CreateRouter();

        router.HandleIncomingMessage("ghost-agent", MakeCommandResult("cmd-1", "abc"));

        Assert.True(true);
    }

    [Fact]
    public void HandleIncomingMessage_NullMessage_DoesNotThrow()
    {
        var socket = new FakeWebSocket();
        _registry.Register("gpu1", MakeConnection("gpu1"), socket);
        var router = CreateRouter();
        _ = router.GetController("agent:gpu1");

        router.HandleIncomingMessage("gpu1", null!);

        Assert.True(true);
    }

    [Fact]
    public void GetController_ReturnsSameInstanceForSameAgent()
    {
        var socket = new FakeWebSocket();
        _registry.Register("gpu1", MakeConnection("gpu1"), socket);
        var router = CreateRouter();

        var first = router.GetController("agent:gpu1");
        var second = router.GetController("agent:gpu1");

        Assert.Same(first, second);
    }
}
