using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services.Scheduler;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests for per-target queue semantics: while a streaming response is being
/// consumed, the target's worker must NOT switch models (which would stop the
/// upstream container mid-stream). The next queued request holds its connection
/// until the previous body is fully drained; only then does the switch run.
/// </summary>
public sealed class SchedulerWorkerDrainGatingTests
{
    private readonly FakeDockerController _docker = new();
    private readonly FakeInferenceProxy _inference = new();
    private readonly FakeHealthChecker _healthChecker = new();
    private readonly FakeLogStore _logStore = new();
    private readonly FakeStatsTracker _statsTracker = new();
    private readonly FakeClock _clock = new();
    private readonly FakeContainerRegistry _containerRegistry = new();
    private readonly ILogger<SchedulerWorker> _logger = new LoggerFactory().CreateLogger<SchedulerWorker>();

    public SchedulerWorkerDrainGatingTests()
    {
        // Lane scheduling routes models through the container registry.
        foreach (var model in new[] { "llama", "mistral" })
        {
            _containerRegistry.CreateAsync(new RegisteredRuntime
            {
                Id = $"reg-{model}",
                DisplayName = model,
                Image = model,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }).GetAwaiter().GetResult();
            _containerRegistry.AddModelMappingAsync($"reg-{model}", model).GetAwaiter().GetResult();
        }
    }

    private SchedulerWorker CreateWorker(
        Channel<InferenceRequest>? channel = null,
        SchedulerSettings? settings = null)
    {
        channel ??= Channel.CreateUnbounded<InferenceRequest>();
        settings ??= new SchedulerSettings { MaxContainerStartRetries = 1 };
        return new SchedulerWorker(channel, _docker, _inference, _healthChecker, _logStore, _statsTracker, _clock, _logger, settings,
            _containerRegistry);
    }

    private static InferenceRequest MakeRequest(string model, string id)
    {
        return new InferenceRequest
        {
            Id = id,
            ModelName = model,
            OriginalJson = "{}",
            Priority = 0,
            EnqueuedAt = DateTimeOffset.UtcNow,
            Tcs = new TaskCompletionSource<InferenceResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
    }

    [Fact]
    public async Task SecondRequest_WaitsUntilFirstStreamDrained_BeforeSwitchingModel()
    {
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var worker = CreateWorker(channel);
        var cts = new CancellationTokenSource();
        worker.Start(cts.Token);

        // Simulates an SSE stream whose body has NOT been fully consumed yet.
        var drainTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstHeadersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _inference.InvokeFunc = (req, ct) =>
        {
            if (req.ModelName == "llama")
            {
                firstHeadersReady.TrySetResult();
                return Task.FromResult(new InferenceResponse
                {
                    StatusCode = 200,
                    TokensGenerated = 5,
                    BodyDrained = drainTcs.Task
                });
            }

            return Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 7 });
        };

        // r1 streams llama; its body is still unconsumed.
        var r1 = MakeRequest("llama", "r1");
        await channel.Writer.WriteAsync(r1);
        await r1.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await firstHeadersReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // r2 wants a different model on the same target: must queue behind r1's drain.
        var r2 = MakeRequest("mistral", "r2");
        await channel.Writer.WriteAsync(r2);

        // Grace period for an incorrect implementation to switch models mid-stream.
        await Task.Delay(300);

        Assert.Empty(_docker.StoppedContainerIds);
        Assert.Equal(["llama"], _docker.StartedModels);
        Assert.False(r2.Tcs.Task.IsCompleted, "r2 must wait while r1's stream is unconsumed");

        // Client finishes consuming r1's body → drain completes → switch proceeds.
        drainTcs.TrySetResult();

        var r2Result = await r2.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, r2Result.StatusCode);
        await Eventually.UntilAsync(() => _docker.StartedModels.Count == 2 && _docker.StoppedContainerIds.Count == 1);
        Assert.Equal(["llama", "mistral"], _docker.StartedModels);
        Assert.Single(_docker.StoppedContainerIds);

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }

    [Fact]
    public async Task NoBodyResponse_DoesNotStallQueue_SwitchRunsImmediately()
    {
        // Buffered responses (BodyDrained null) must not stall the queue:
        // the switch runs as soon as the previous request completes.
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var worker = CreateWorker(channel);
        var cts = new CancellationTokenSource();
        worker.Start(cts.Token);

        await channel.Writer.WriteAsync(MakeRequest("llama", "r1"));
        var r2 = MakeRequest("mistral", "r2");
        await channel.Writer.WriteAsync(r2);

        var r2Result = await r2.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(200, r2Result.StatusCode);
        await Eventually.UntilAsync(() => _docker.StartedModels.Count == 2 && _docker.StoppedContainerIds.Count == 1);
        Assert.Equal(["llama", "mistral"], _docker.StartedModels);
        Assert.Single(_docker.StoppedContainerIds);

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }

    [Fact]
    public async Task DifferentTargets_ProcessConcurrently_AgentStreamDoesNotBlockHost()
    {
        // User scenario: agent A runs model-0 while host runs model-1.
        // Queues are PER TARGET — an undrained agent stream must not delay host requests.
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var agentDocker = new FakeDockerController { IdPrefix = "agent" };
        var hostDocker = new FakeDockerController { IdPrefix = "host" };
        var router = new FakeDockerControllerRouter(new Dictionary<string, IDockerController>
        {
            ["agent-a"] = agentDocker,
            ["host"] = hostDocker
        });
        var resolver = new FakeModelTargetResolver();
        resolver.ResolveFunc = (model, ct) =>
            Task.FromResult(model == "model-0" ? "agent-a" : "host");
        var registry = new FakeContainerRegistry();

        // Lane scheduling routes models through the container registry.
        await registry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-0", DisplayName = "model-0", Image = "model-0",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await registry.AddModelMappingAsync("reg-0", "model-0");
        await registry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-1", DisplayName = "model-1", Image = "model-1",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await registry.AddModelMappingAsync("reg-1", "model-1");

        var worker = new SchedulerWorker(
            channel, hostDocker, _inference, _healthChecker,
            _logStore, _statsTracker, _clock, _logger,
            new SchedulerSettings { MaxContainerStartRetries = 1 },
            registry, router, resolver);
        var cts = new CancellationTokenSource();
        worker.Start(cts.Token);

        var agentDrainTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentHeadersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _inference.InvokeFunc = (req, ct) =>
        {
            if (req.ModelName == "model-0")
            {
                agentHeadersReady.TrySetResult();
                return Task.FromResult(new InferenceResponse
                {
                    StatusCode = 200,
                    TokensGenerated = 1,
                    BodyDrained = agentDrainTcs.Task
                });
            }

            return Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 2 });
        };

        // Agent A starts streaming model-0 and stays undrained.
        var r1 = MakeRequest("model-0", "r1");
        await channel.Writer.WriteAsync(r1);
        await r1.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await agentHeadersReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Host receives model-1: must run immediately despite the agent's active stream.
        var r2 = MakeRequest("model-1", "r2");
        await channel.Writer.WriteAsync(r2);
        var r2Result = await r2.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(200, r2Result.StatusCode);
        await Eventually.UntilAsync(() => hostDocker.StartedModels.Contains("model-1"));
        Assert.Contains("model-1", hostDocker.StartedModels);
        Assert.Empty(agentDocker.StoppedContainerIds);

        // Release the agent stream and shut down.
        agentDrainTcs.TrySetResult();
        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }

    [Fact]
    public async Task HostQueue_ModelSwitchWaitsForDrain_ThenStopsOldStartsNew()
    {
        // Exact user sequence on one target: model-1 runs; model-2 queues with its
        // connection held; when model-1 finishes draining → stop model-1's container
        // → start model-2 → process. Uses the container-aware switch path.
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var hostDocker = new FakeDockerController { IdPrefix = "host" };
        var router = new FakeDockerControllerRouter(new Dictionary<string, IDockerController>
        {
            ["host"] = hostDocker
        });
        var resolver = new FakeModelTargetResolver();
        resolver.ResolveFunc = (model, ct) => Task.FromResult("host");
        var registry = new FakeContainerRegistry();

        await registry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-1",
            DisplayName = "M1",
            Image = "m1:latest",
            RuntimeContainerId = "host-m1",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await registry.AddModelMappingAsync("reg-1", "model-1");
        await registry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-2",
            DisplayName = "M2",
            Image = "m2:latest",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await registry.AddModelMappingAsync("reg-2", "model-2");

        var worker = new SchedulerWorker(
            channel, hostDocker, _inference, _healthChecker,
            _logStore, _statsTracker, _clock, _logger,
            new SchedulerSettings { MaxContainerStartRetries = 1 },
            registry, router, resolver);
        var cts = new CancellationTokenSource();
        worker.Start(cts.Token);

        var drainTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _inference.InvokeFunc = (req, ct) =>
        {
            if (req.ModelName == "model-1")
            {
                return Task.FromResult(new InferenceResponse
                {
                    StatusCode = 200,
                    TokensGenerated = 1,
                    BodyDrained = drainTcs.Task
                });
            }

            return Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 2 });
        };

        var r1 = MakeRequest("model-1", "r1");
        await channel.Writer.WriteAsync(r1);
        await r1.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var r2 = MakeRequest("model-2", "r2");
        await channel.Writer.WriteAsync(r2);

        // While model-1's stream is unconsumed: nothing stopped, model-2 still waiting.
        await Task.Delay(300);
        Assert.Empty(hostDocker.StoppedContainerIds);
        Assert.False(r2.Tcs.Task.IsCompleted, "model-2 must wait until model-1 fully drains");

        drainTcs.TrySetResult();

        var r2Result = await r2.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, r2Result.StatusCode);
        await Eventually.UntilAsync(() => hostDocker.StoppedContainerIds.Count == 1);
        Assert.Single(hostDocker.StoppedContainerIds);

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }
}
