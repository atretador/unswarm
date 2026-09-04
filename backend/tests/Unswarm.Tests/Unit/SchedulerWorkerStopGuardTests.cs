using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services.Scheduler;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests for the registered-only stop guard: unregistered running containers on the host
/// are NOT stopped during a model switch; registered incompatible ones ARE.
/// </summary>
public sealed class SchedulerWorkerStopGuardTests : IDisposable
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
        IDockerController docker,
        IDockerControllerRouter router,
        IModelTargetResolver resolver,
        SchedulerSettings? settings = null)
    {
        _channel = Channel.CreateUnbounded<InferenceRequest>();
        settings ??= new SchedulerSettings { MaxContainerStartRetries = 1 };
        _worker = new SchedulerWorker(
            _channel, docker, _inference, _healthChecker,
            _logStore, _statsTracker, _clock, _logger, settings,
            Options.Create(new ContainerHostOptions()), _containerRegistry, router, resolver);
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

    private static FakeDockerControllerRouter SingleHostRouter(FakeDockerController host)
    {
        return new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = host });
    }

    private static FakeModelTargetResolver HostResolver()
    {
        var resolver = new FakeModelTargetResolver();
        resolver.ResolveFunc = (model, ct) => Task.FromResult("host");
        return resolver;
    }

    [Fact]
    public async Task UnregisteredContainer_NotStoppedDuringSwitch()
    {
        // Register model-a on reg-a with known RuntimeContainerId
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-a", DisplayName = "A", Image = "a:latest",
            RuntimeContainerId = "host-1",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await _containerRegistry.AddModelMappingAsync("reg-a", "model-a");

        // Register model-b on reg-b (will be the target)
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-b", DisplayName = "B", Image = "b:latest",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await _containerRegistry.AddModelMappingAsync("reg-b", "model-b");

        var host = new FakeDockerController { IdPrefix = "host" };
        // Add an unregistered extra container to the host that should NOT be stopped
        host.ListedContainers.Add(new ContainerInfo
        {
            Id = "orphan-container-99",
            ModelId = "unknown",
            ModelName = "unknown",
            Status = ContainerStatus.Running
        });

        CreateWorker(host, SingleHostRouter(host), HostResolver());

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

        await Eventually.UntilAsync(() => host.StartedModels.Count == 2 && host.StoppedContainerIds.Count == 1);
        // model-a started, then stopped (incompatible with model-b), model-b started.
        // The orphan container should NOT appear in stopped list.
        Assert.Equal(["a:latest", "b:latest"], host.StartedModels);
        Assert.Single(host.StoppedContainerIds);
        Assert.DoesNotContain("orphan-container-99", host.StoppedContainerIds);

        await ShutdownAsync();
    }

    [Fact]
    public async Task RegisteredIncompatibleContainer_IsStopped()
    {
        // Register model-a on reg-a and model-b on reg-b, both incompatible
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-a", DisplayName = "A", Image = "a:latest",
            RuntimeContainerId = "host-registered-a",
            CanRunAlongWith = [],
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-b", DisplayName = "B", Image = "b:latest",
            CanRunAlongWith = [],
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await _containerRegistry.AddModelMappingAsync("reg-a", "model-a");
        await _containerRegistry.AddModelMappingAsync("reg-b", "model-b");

        var host = new FakeDockerController { IdPrefix = "host" };
        CreateWorker(host, SingleHostRouter(host), HostResolver());

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

        await Eventually.UntilAsync(() => host.StartedModels.Count == 2 && host.StoppedContainerIds.Count == 1);
        // Registered incompatible container IS stopped
        Assert.Equal(["a:latest", "b:latest"], host.StartedModels);
        Assert.Single(host.StoppedContainerIds);

        await ShutdownAsync();
    }

    [Fact]
    public async Task RecreatedContainer_IsStoppedByNewIdDuringSwitch()
    {
        // The docker container for reg-a was recreated outside the scheduler:
        // same name "a:latest", NEW id. The slot bookkeeping still holds the id
        // captured at start time — the stop must target the live container.
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-a", DisplayName = "A", Image = "a:latest",
            RuntimeContainerId = "old-id",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await _containerRegistry.AddModelMappingAsync("reg-a", "model-a");

        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-b", DisplayName = "B", Image = "b:latest",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await _containerRegistry.AddModelMappingAsync("reg-b", "model-b");

        var host = new FakeDockerController { IdPrefix = "host" };
        host.ListedContainers.Add(new ContainerInfo
        {
            Id = "new-id",
            ModelId = "a:latest",
            ModelName = "a:latest",
            Status = ContainerStatus.Running
        });

        CreateWorker(host, SingleHostRouter(host), HostResolver());

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

        await Eventually.UntilAsync(() => host.StoppedContainerIds.Count == 1);
        // The incompatible container was stopped via its LIVE id, not the stale one.
        var stopped = Assert.Single(host.StoppedContainerIds);
        Assert.Equal("new-id", stopped);

        await ShutdownAsync();
    }

    [Fact]
    public async Task SchedulerStart_RefreshesStalePersistedRuntimeContainerId()
    {
        // Registry holds a stale RuntimeContainerId; a scheduler-driven start resolves
        // the live container by name and must persist the new id back to the registry.
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-a", DisplayName = "A", Image = "a:latest",
            RuntimeContainerId = "old-id",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await _containerRegistry.AddModelMappingAsync("reg-a", "model-a");

        var host = new FakeDockerController { IdPrefix = "host" };
        CreateWorker(host, SingleHostRouter(host), HostResolver());

        var allDone = new TaskCompletionSource();
        _inference.InvokeFunc = (req, ct) =>
        {
            allDone.TrySetResult();
            return Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });
        };

        await EnqueueAsync(MakeRequest("model-a", id: "r1"));
        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Eventually.UntilAsync(() => host.StartedContainerIds.Count == 1);
        var persisted = await _containerRegistry.GetAsync("reg-a");
        Assert.NotNull(persisted);
        var startedId = Assert.Single(host.StartedContainerIds);
        Assert.NotEqual("old-id", startedId);
        Assert.Equal(startedId, persisted!.RuntimeContainerId);

        await ShutdownAsync();
    }

    public void Dispose()
    {
    }
}
