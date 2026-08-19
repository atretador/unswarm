using System.Text.Json;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Models;
using Unswarm.Core.Services.Remote;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class RemoteAgentDockerControllerTests
{
    private readonly FakeAgentRegistry _registry = new();
    private readonly ILogger<RemoteAgentDockerController> _logger =
        new LoggerFactory().CreateLogger<RemoteAgentDockerController>();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private RemoteAgentDockerController CreateController(TimeSpan? timeout = null)
    {
        _registry.Register("gpu1", new AgentConnection
        {
            Name = "gpu1",
            ConnectionId = "cid-1",
            ConnectedAt = DateTimeOffset.UtcNow,
            IsConnected = true
        }, new FakeWebSocket());
        return new RemoteAgentDockerController("gpu1", _registry, _logger, timeout);
    }

    private static AgentMessage MakeReply(string commandId, object payload)
        => new()
        {
            Type = RemoteAgentDockerController.CommandResultType,
            Id = commandId,
            Agent = "gpu1",
            Payload = JsonSerializer.SerializeToElement(payload, JsonOptions)
        };

    [Fact]
    public async Task StartContainer_SendsCommandAndMapsResult()
    {
        var controller = CreateController();
        _registry.OnSend = msg =>
        {
            var reply = MakeReply(msg.Id!, new { containerId = "abc123", mappedPort = 8080 });
            controller.HandleIncomingMessage(reply);
            return Task.FromResult<AgentMessage?>(null);
        };

        var result = await controller.StartContainerAsync("vllm-1");

        Assert.Equal("abc123", result.ContainerId);
        Assert.Equal(8080, result.MappedPort);
        Assert.Null(result.ErrorMessage);

        // Verify the wire command
        Assert.True(_registry.SentMessages.TryDequeue(out var sent));
        Assert.Equal(RemoteAgentDockerController.CommandType, sent.Type);
        Assert.NotNull(sent.Id);
        Assert.Equal("gpu1", sent.Agent);
        Assert.Equal("start_container", sent.Payload!.Value.GetProperty("command").GetString());
        Assert.Equal("vllm-1", sent.Payload.Value.GetProperty("image").GetString());
        Assert.Equal(8080, sent.Payload.Value.GetProperty("containerPort").GetInt32());
    }

    [Fact]
    public async Task StartContainer_ErrorResult_MapsErrorMessage()
    {
        var controller = CreateController();
        _registry.OnSend = msg =>
        {
            var reply = MakeReply(msg.Id!, new { containerId = "", error = "container not found" });
            controller.HandleIncomingMessage(reply);
            return Task.FromResult<AgentMessage?>(null);
        };

        var result = await controller.StartContainerAsync("missing");

        Assert.Equal("container not found", result.ErrorMessage);
        Assert.Null(result.MappedPort);
    }

    [Fact]
    public async Task StopContainer_SendsStopCommand()
    {
        var controller = CreateController();
        _registry.OnSend = msg =>
        {
            controller.HandleIncomingMessage(MakeReply(msg.Id!, new { }));
            return Task.FromResult<AgentMessage?>(null);
        };

        await controller.StopContainerAsync("vllm-1");

        Assert.True(_registry.SentMessages.TryDequeue(out var sent));
        Assert.Equal("stop_container", sent.Payload!.Value.GetProperty("command").GetString());
        Assert.Equal("vllm-1", sent.Payload.Value.GetProperty("containerId").GetString());
    }

    [Fact]
    public async Task InspectContainer_MapsResult()
    {
        var controller = CreateController();
        _registry.OnSend = msg =>
        {
            var reply = MakeReply(msg.Id!, new { status = "running", pid = 4242, memoryMb = 4096, cpuPercent = 12.5, uptimeSeconds = 99 });
            controller.HandleIncomingMessage(reply);
            return Task.FromResult<AgentMessage?>(null);
        };

        var result = await controller.InspectContainerAsync("vllm-1");

        Assert.NotNull(result);
        Assert.Equal("running", result.Status);
        Assert.Equal(4242, result.Pid);
        Assert.Equal(4096, result.MemoryMb);
        Assert.Equal(12.5, result.CpuPercent);
        Assert.Equal(99, result.UptimeSeconds);
    }

    [Fact]
    public async Task ListContainers_MapsResult()
    {
        var controller = CreateController();
        _registry.OnSend = msg =>
        {
            var reply = MakeReply(msg.Id!, new
            {
                containers = new object[]
                {
                    new { id = "c1", modelName = "llama", status = "running", port = (int?)8080, registeredContainerId = (string?)"reg-1" },
                    new { id = "c2", modelName = "mistral", status = "exited", port = (int?)null, registeredContainerId = (string?)null }
                }
            });
            controller.HandleIncomingMessage(reply);
            return Task.FromResult<AgentMessage?>(null);
        };

        var result = await controller.ListContainersAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal("c1", result[0].Id);
        Assert.Equal("llama", result[0].ModelName);
        Assert.Equal(ContainerStatus.Running, result[0].Status);
        Assert.Equal(8080, result[0].Port);
        Assert.Equal("reg-1", result[0].RegisteredContainerId);
        Assert.Equal(ContainerStatus.Stopped, result[1].Status);
    }

    [Fact]
    public async Task GetContainerLogs_MapsResult()
    {
        var controller = CreateController();
        _registry.OnSend = msg =>
        {
            var reply = MakeReply(msg.Id!, new { logs = new[] { "line1", "line2" } });
            controller.HandleIncomingMessage(reply);
            return Task.FromResult<AgentMessage?>(null);
        };

        var result = await controller.GetContainerLogsAsync("vllm-1", tailLines: 50);

        Assert.Equal(["line1", "line2"], result);
        Assert.True(_registry.SentMessages.TryDequeue(out var sent));
        Assert.Equal(50, sent.Payload!.Value.GetProperty("tailLines").GetInt32());
    }

    [Fact]
    public async Task CommandTimeout_TimesOut()
    {
        var controller = CreateController(timeout: TimeSpan.FromMilliseconds(100));
        // No reply delivered → pending TCS times out

        await Assert.ThrowsAsync<TimeoutException>(
            () => controller.StartContainerAsync("vllm-1"));
    }

    [Fact]
    public async Task NotConnected_Throws()
    {
        _registry.Connected = false;
        var controller = new RemoteAgentDockerController("gpu1", _registry, _logger);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.StartContainerAsync("vllm-1"));
        Assert.Contains("not connected", ex.Message);
    }

    [Fact]
    public async Task Correlation_CompletesByCommandId_OutOfOrder()
    {
        var controller = CreateController();
        var sentIds = new List<string>();
        _registry.OnSend = msg =>
        {
            lock (sentIds) sentIds.Add(msg.Id!);
            return Task.FromResult<AgentMessage?>(null);
        };

        var startTask = controller.StartContainerAsync("vllm-1");
        var stopTask = controller.StopContainerAsync("vllm-2");

        // Wait for both commands to be sent
        while (sentIds.Count < 2)
            await Task.Delay(10);

        string id1, id2;
        lock (sentIds) { id1 = sentIds[0]; id2 = sentIds[1]; }

        // Deliver replies out of order
        controller.HandleIncomingMessage(MakeReply(id2, new { }));
        controller.HandleIncomingMessage(MakeReply(id1, new { containerId = "abc123", mappedPort = 8080 }));

        await stopTask;
        var startResult = await startTask;

        Assert.Equal("abc123", startResult.ContainerId);
        Assert.Equal(0, controller.PendingCommandCount);
    }

    [Fact]
    public async Task HealthCheck_MapsHealthyFlag()
    {
        var controller = CreateController();
        _registry.OnSend = msg =>
        {
            var reply = MakeReply(msg.Id!, new { healthy = true });
            controller.HandleIncomingMessage(reply);
            return Task.FromResult<AgentMessage?>(null);
        };

        var healthy = await controller.HealthCheckAsync(8080);

        Assert.True(healthy);
        Assert.True(_registry.SentMessages.TryDequeue(out var sent));
        Assert.Equal("health_check", sent.Payload!.Value.GetProperty("command").GetString());
        Assert.Equal(8080, sent.Payload.Value.GetProperty("port").GetInt32());
    }

    [Fact]
    public async Task DiscoverModels_MapsResult()
    {
        var controller = CreateController();
        _registry.OnSend = msg =>
        {
            var reply = MakeReply(msg.Id!, new { models = new[] { new { modelId = "llama-3", ownedBy = "meta" } } });
            controller.HandleIncomingMessage(reply);
            return Task.FromResult<AgentMessage?>(null);
        };

        var result = await controller.DiscoverModelsAsync(8080);

        var model = Assert.Single(result);
        Assert.Equal("llama-3", model.ModelId);
        Assert.Equal("meta", model.OwnedBy);
    }

    [Fact]
    public async Task DiscoverModels_ParsesRawOpenAIShape()
    {
        var controller = CreateController();
        _registry.OnSend = msg =>
        {
            // The Go agent returns the raw OpenAI /v1/models body: { data: [ { id, owned_by } ] }
            var reply = MakeReply(msg.Id!, new
            {
                data = new object[]
                {
                    new { id = "llama-3-8b", owned_by = "meta" },
                    new { id = "mistral-7b", owned_by = "mistralai" }
                }
            });
            controller.HandleIncomingMessage(reply);
            return Task.FromResult<AgentMessage?>(null);
        };

        var result = await controller.DiscoverModelsAsync(8080);

        Assert.Equal(2, result.Count);
        Assert.Equal("llama-3-8b", result[0].ModelId);
        Assert.Equal("meta", result[0].OwnedBy);
        Assert.Equal("mistral-7b", result[1].ModelId);
        Assert.Equal("mistralai", result[1].OwnedBy);
    }

    [Fact]
    public async Task DiscoverModels_PrefersRawOpenAIShape_WhenBothPresent()
    {
        var controller = CreateController();
        _registry.OnSend = msg =>
        {
            var reply = MakeReply(msg.Id!, new
            {
                data = new object[]
                {
                    new { id = "from-data", owned_by = "org" }
                },
                models = new object[]
                {
                    new { modelId = "from-models", ownedBy = "legacy" }
                }
            });
            controller.HandleIncomingMessage(reply);
            return Task.FromResult<AgentMessage?>(null);
        };

        var result = await controller.DiscoverModelsAsync(8080);

        var model = Assert.Single(result);
        Assert.Equal("from-data", model.ModelId);
    }

    [Fact]
    public async Task DiscoverModels_SkipsInvalidEntries()
    {
        var controller = CreateController();
        _registry.OnSend = msg =>
        {
            var reply = MakeReply(msg.Id!, new
            {
                data = new object[]
                {
                    new { id = "", owned_by = "meta" },
                    new { id = "valid-model", owned_by = "openai" },
                    "not-an-object"
                }
            });
            controller.HandleIncomingMessage(reply);
            return Task.FromResult<AgentMessage?>(null);
        };

        var result = await controller.DiscoverModelsAsync(8080);

        var model = Assert.Single(result);
        Assert.Equal("valid-model", model.ModelId);
    }
}