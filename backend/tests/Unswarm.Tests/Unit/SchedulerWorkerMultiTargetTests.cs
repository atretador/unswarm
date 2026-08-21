using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services.Scheduler;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Multi-target scheduler behavior: cross-target concurrency, per-target single-slot
/// switching, canRunAlongWith compatibility, agent routing, and target-scoped failures.
/// </summary>
public sealed class SchedulerWorkerMultiTargetTests : IDisposable
{
    private readonly FakeInferenceProxy _inference = new();
    private readonly FakeHealthChecker _healthChecker = new();
    private readonly FakeLogStore _logStore = new();
    private readonly FakeStatsTracker _statsTracker = new();
    private readonly FakeClock _clock = new();
    private readonly FakeContainerRegistry _containerRegistry = new();
    private readonly ILogger<SchedulerWorker> _logger = new LoggerFactory().CreateLogger<SchedulerWorker>();

    private Channel<InferenceRequest> _channel = Channel.CreateUnbounded<InferenceRequest>();
    private SchedulerWorker? _worker;
    private CancellationTokenSource? _cts;

    private SchedulerWorker CreateWorker(
        IDockerControllerRouter router,
        IModelTargetResolver resolver,
        SchedulerSettings? settings = null)
    {
        _channel = Channel.CreateUnbounded<InferenceRequest>();
        settings ??= new SchedulerSettings { MaxContainerStartRetries = 1 };
        _worker = new SchedulerWorker(
            _channel, new FakeDockerController(), _inference, _healthChecker,
            _logStore, _statsTracker, _clock, _logger, settings,
            _containerRegistry, router, resolver);
        _cts = new CancellationTokenSource();
        _worker.Start(_cts.Token);
        return _worker;
    }

    private async Task EnqueueAsync(params InferenceRequest[] requests)
    {
        foreach (var request in requests)
        {
            await _channel.Writer.WriteAsync(request);
        }
    }

    private async Task ShutdownAsync()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            if (_worker is not null)
                await _worker.WaitForShutdownAsync();
        }
    }

    private static InferenceRequest MakeRequest(string model, string? id = null)
    {
        return new InferenceRequest
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            ModelName = model,
            OriginalJson = "{}",
            Priority = 0,
            EnqueuedAt = DateTimeOffset.UtcNow,
            Tcs = new TaskCompletionSource<InferenceResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
    }

    private static FakeDockerControllerRouter HostAndAgentRouter(
        FakeDockerController host,
        FakeDockerController agent,
        bool agentReachable = true)
    {
        return new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController>
            {
                ["host"] = host,
                ["agent:gpu1"] = agent
            },
            reachable: agentReachable ? null : ["host"]);
    }

    private static FakeModelTargetResolver HostAndAgentResolver()
    {
        var resolver = new FakeModelTargetResolver();
        resolver.ResolveFunc = (model, ct) => Task.FromResult(
            model.StartsWith("agent-", StringComparison.Ordinal)
                ? "agent:gpu1"
                : "host");
        return resolver;
    }

    [Fact]
    public async Task CrossTarget_ProcessesConcurrently()
    {
        var host = new FakeDockerController { IdPrefix = "host" };
        var agent = new FakeDockerController { IdPrefix = "agent" };
        CreateWorker(HostAndAgentRouter(host, agent), HostAndAgentResolver());

        var bothInFlight = new TaskCompletionSource();
        var inFlight = new ConcurrentDictionary<string, byte>();
        _inference.InvokeFunc = async (req, ct) =>
        {
            inFlight[req.ModelName] = 0;
            if (inFlight.ContainsKey("host-a") && inFlight.ContainsKey("agent-a"))
                bothInFlight.TrySetResult();
            await Task.Delay(200, ct);
            return new InferenceResponse { StatusCode = 200, TokensGenerated = 1 };
        };

        var r1 = MakeRequest("host-a", id: "r1");
        var r2 = MakeRequest("agent-a", id: "r2");
        await EnqueueAsync(r1, r2);

        await bothInFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(r1.Tcs.Task, r2.Tcs.Task).WaitAsync(TimeSpan.FromSeconds(5));

        await ShutdownAsync();
    }

    [Fact]
    public async Task CrossTarget_ModelSwitches_DoNotStopOtherTargetsContainers()
    {
        var host = new FakeDockerController { IdPrefix = "host" };
        var agent = new FakeDockerController { IdPrefix = "agent" };
        CreateWorker(HostAndAgentRouter(host, agent), HostAndAgentResolver());

        var allDone = new TaskCompletionSource();
        var remaining = 4;
        _inference.InvokeFunc = (req, ct) =>
        {
            if (Interlocked.Decrement(ref remaining) == 0)
                allDone.TrySetResult();
            return Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });
        };

        await EnqueueAsync(
            MakeRequest("host-a", id: "r1"),
            MakeRequest("agent-a", id: "r2"),
            MakeRequest("host-b", id: "r3"),
            MakeRequest("agent-b", id: "r4"));

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Each target switched its own model: exactly one stop per target, its own container
        Assert.Equal(["host-a", "host-b"], host.StartedModels);
        Assert.Equal(["agent-a", "agent-b"], agent.StartedModels);

        Assert.Single(host.StoppedContainerIds);
        Assert.StartsWith("host-", host.StoppedContainerIds[0]);
        Assert.Single(agent.StoppedContainerIds);
        Assert.StartsWith("agent-", agent.StoppedContainerIds[0]);

        // No cross-target contamination
        Assert.All(host.StoppedContainerIds, id => Assert.StartsWith("host-", id));
        Assert.All(agent.StoppedContainerIds, id => Assert.StartsWith("agent-", id));

        await ShutdownAsync();
    }

    [Fact]
    public async Task SameTarget_ModelSwitch_StopsOldContainer()
    {
        var host = new FakeDockerController { IdPrefix = "host" };
        var agent = new FakeDockerController { IdPrefix = "agent" };
        CreateWorker(HostAndAgentRouter(host, agent), HostAndAgentResolver());

        var allDone = new TaskCompletionSource();
        var remaining = 2;
        _inference.InvokeFunc = (req, ct) =>
        {
            if (Interlocked.Decrement(ref remaining) == 0)
                allDone.TrySetResult();
            return Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });
        };

        await EnqueueAsync(MakeRequest("host-a", id: "r1"), MakeRequest("host-b", id: "r2"));

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Same-target stop/start preserved: old container stopped, new started
        Assert.Equal(["host-a", "host-b"], host.StartedModels);
        Assert.Single(host.StoppedContainerIds);
        Assert.Empty(agent.StartedModels);
        Assert.Empty(agent.StoppedContainerIds);

        await ShutdownAsync();
    }

    [Fact]
    public async Task CanRunAlongWith_CompatibleContainersStayRunning()
    {
        await RegisterContainerAsync("reg-a", "container-a", ["container-b"]);
        await RegisterContainerAsync("reg-b", "container-b", ["container-a"]);
        await _containerRegistry.AddModelMappingAsync("reg-a", "model-a");
        await _containerRegistry.AddModelMappingAsync("reg-b", "model-b");

        var host = new FakeDockerController { IdPrefix = "host" };
        CreateWorker(HostAndAgentRouter(host, new FakeDockerController()), HostAndAgentResolver());

        var allDone = new TaskCompletionSource();
        var remaining = 2;
        _inference.InvokeFunc = (req, ct) =>
        {
            if (Interlocked.Decrement(ref remaining) == 0)
                allDone.TrySetResult();
            return Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });
        };

        await EnqueueAsync(MakeRequest("model-a", id: "r1"), MakeRequest("model-b", id: "r2"));

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Both containers started, neither stopped (symmetric compatibility)
        Assert.Equal(["container-a", "container-b"], host.StartedModels);
        Assert.Empty(host.StoppedContainerIds);

        await ShutdownAsync();
    }

    [Fact]
    public async Task CanRunAlongWith_IncompatibleContainersStopOldOne()
    {
        // Empty canRunAlongWith = single-container mode: must run alone on its agent
        await RegisterContainerAsync("reg-a", "container-a", []);
        await RegisterContainerAsync("reg-b", "container-b", []);
        await _containerRegistry.AddModelMappingAsync("reg-a", "model-a");
        await _containerRegistry.AddModelMappingAsync("reg-b", "model-b");

        var host = new FakeDockerController { IdPrefix = "host" };
        CreateWorker(HostAndAgentRouter(host, new FakeDockerController()), HostAndAgentResolver());

        var allDone = new TaskCompletionSource();
        var remaining = 2;
        _inference.InvokeFunc = (req, ct) =>
        {
            if (Interlocked.Decrement(ref remaining) == 0)
                allDone.TrySetResult();
            return Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });
        };

        await EnqueueAsync(MakeRequest("model-a", id: "r1"), MakeRequest("model-b", id: "r2"));

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Incompatible → old container stopped before starting the new one
        Assert.Equal(["container-a", "container-b"], host.StartedModels);
        Assert.Single(host.StoppedContainerIds);

        await ShutdownAsync();
    }

    [Fact]
    public async Task CanRunAlongWith_CompatibleThenIncompatible_StopsCompatibleOnes()
    {
        // A and B are compatible; C is incompatible with both
        await RegisterContainerAsync("reg-a", "container-a", ["container-b"]);
        await RegisterContainerAsync("reg-b", "container-b", ["container-a"]);
        await RegisterContainerAsync("reg-c", "container-c", []);
        await _containerRegistry.AddModelMappingAsync("reg-a", "model-a");
        await _containerRegistry.AddModelMappingAsync("reg-b", "model-b");
        await _containerRegistry.AddModelMappingAsync("reg-c", "model-c");

        var host = new FakeDockerController { IdPrefix = "host" };
        CreateWorker(HostAndAgentRouter(host, new FakeDockerController()), HostAndAgentResolver());

        var allDone = new TaskCompletionSource();
        var remaining = 3;
        _inference.InvokeFunc = (req, ct) =>
        {
            if (Interlocked.Decrement(ref remaining) == 0)
                allDone.TrySetResult();
            return Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });
        };

        await EnqueueAsync(
            MakeRequest("model-a", id: "r1"),
            MakeRequest("model-b", id: "r2"),
            MakeRequest("model-c", id: "r3"));

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // model-c is incompatible with both a and b → both stopped before c starts
        Assert.Equal(["container-a", "container-b", "container-c"], host.StartedModels);
        Assert.Equal(2, host.StoppedContainerIds.Count);

        await ShutdownAsync();
    }

    [Fact]
    public async Task AgentTarget_Routing_ResolvesToAgent()
    {
        var host = new FakeDockerController { IdPrefix = "host" };
        var agent = new FakeDockerController { IdPrefix = "agent" };
        CreateWorker(HostAndAgentRouter(host, agent), HostAndAgentResolver());

        var req = MakeRequest("agent-remote", id: "r1");
        await EnqueueAsync(req);

        var result = await req.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("agent:gpu1", req.TargetId);
        Assert.Equal(["agent-remote"], agent.StartedModels);
        Assert.Empty(host.StartedModels);

        await ShutdownAsync();
    }

    [Fact]
    public async Task ErrorState_FailsOnlyThatTargetsRequests()
    {
        var host = new FakeDockerController { IdPrefix = "host" };
        var agent = new FakeDockerController { IdPrefix = "agent" };
        host.OnStart = (model, ct) => model == "host-broken"
            ? Task.FromResult(new ContainerStartResult { ContainerId = "fail", ErrorMessage = "Container start failed" })
            : Task.FromResult(new ContainerStartResult { ContainerId = $"host-{model}", MappedPort = 9000 });

        CreateWorker(HostAndAgentRouter(host, agent), HostAndAgentResolver());

        var r1 = MakeRequest("host-broken", id: "r1");
        var r2 = MakeRequest("agent-good", id: "r2");
        var r3 = MakeRequest("host-ok", id: "r3");

        await EnqueueAsync(r1, r2, r3);

        // r1 fails (host target error)
        await Assert.ThrowsAsync<InvalidOperationException>(() => r1.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        // r2 on the agent target succeeds despite host failure
        var result2 = await r2.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, result2.StatusCode);
        // r3 on the host target recovers
        var result3 = await r3.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, result3.StatusCode);

        Assert.Equal(["agent-good"], agent.StartedModels);

        await ShutdownAsync();
    }

    [Fact]
    public async Task DisconnectedAgentTarget_FailsRequestFast()
    {
        var host = new FakeDockerController { IdPrefix = "host" };
        var agent = new FakeDockerController { IdPrefix = "agent" };
        CreateWorker(HostAndAgentRouter(host, agent, agentReachable: false), HostAndAgentResolver());

        var remote = MakeRequest("agent-remote", id: "r1");
        var local = MakeRequest("host-local", id: "r2");

        await EnqueueAsync(remote, local);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => remote.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("not reachable", ex.Message);

        var result = await local.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, result.StatusCode);

        await ShutdownAsync();
    }

    [Fact]
    public async Task MaxConcurrentTargets_LimitsDistinctTargets()
    {
        var host = new FakeDockerController { IdPrefix = "host" };
        var agent = new FakeDockerController { IdPrefix = "agent" };
        CreateWorker(HostAndAgentRouter(host, agent), HostAndAgentResolver(),
            settings: new SchedulerSettings { MaxConcurrentTargets = 1, MaxContainerStartRetries = 1 });

        var r1 = MakeRequest("host-a", id: "r1");
        var r2 = MakeRequest("agent-a", id: "r2");

        await EnqueueAsync(r1, r2);

        // First target accepted
        var result1 = await r1.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, result1.StatusCode);

        // Second distinct target rejected
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => r2.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("Max concurrent targets", ex.Message);

        await ShutdownAsync();
    }

    private async Task RegisterContainerAsync(string id, string image, IReadOnlyList<string> canRunAlongWith)
    {
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = id,
            DisplayName = image,
            Image = image,
            CanRunAlongWith = canRunAlongWith,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    public void Dispose()
    {
    }
}