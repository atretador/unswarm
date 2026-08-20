using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services.Scheduler;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests that the SchedulerWorker correctly handles container-aware model switching:
/// - Same registered container = instant switch (no Docker stop/start)
/// - Different registered containers = full stop/start cycle
/// </summary>
public sealed class SchedulerWorkerContainerAwareTests : IDisposable
{
    private readonly FakeDockerController _docker = new();
    private readonly FakeInferenceProxy _inference = new();
    private readonly FakeHealthChecker _healthChecker = new();
    private readonly FakeLogStore _logStore = new();
    private readonly FakeStatsTracker _statsTracker = new();
    private readonly FakeClock _clock = new();
    private readonly FakeContainerRegistry _containerRegistry = new();
    private readonly ILogger<SchedulerWorker> _logger = new LoggerFactory().CreateLogger<SchedulerWorker>();

    private SchedulerWorker CreateWorker(
        Channel<InferenceRequest>? channel = null,
        SchedulerSettings? settings = null)
    {
        channel ??= Channel.CreateUnbounded<InferenceRequest>();
        settings ??= new SchedulerSettings();
        return new SchedulerWorker(
            channel, _docker, _inference, _healthChecker,
            _logStore, _statsTracker, _clock, _logger, settings,
            _containerRegistry);
    }

    private static InferenceRequest MakeRequest(
        string model = "llama",
        string? id = null)
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

    [Fact]
    public async Task SameContainer_ModelSwitch_IsInstant_NoDockerStopStart()
    {
        // Register two models on the same container
        const string containerId = "registered-1";
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = containerId,
            DisplayName = "Multi-model",
            Image = "multi:latest",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _containerRegistry.AddModelMappingAsync(containerId, "model-a");
        await _containerRegistry.AddModelMappingAsync(containerId, "model-b");

        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var worker = CreateWorker(channel);
        var cts = new CancellationTokenSource();
        worker.Start(cts.Token);

        var allDone = new TaskCompletionSource();
        var remaining = 2;

        _inference.InvokeFunc = (req, ct) =>
        {
            if (Interlocked.Decrement(ref remaining) == 0)
                allDone.TrySetResult();
            return Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });
        };

        await channel.Writer.WriteAsync(MakeRequest("model-a", id: "r1"));
        await channel.Writer.WriteAsync(MakeRequest("model-b", id: "r2"));

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // First model: full Docker start + health check
        Assert.Single(_docker.StartedModels);
        Assert.Equal("model-a", _docker.StartedModels[0]);

        // Second model: instant switch, NO Docker stop/start
        Assert.Empty(_docker.StoppedContainerIds);
        // Only one StartedModels call (for model-a); model-b was instant
        Assert.Single(_docker.StartedModels);

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }

    [Fact]
    public async Task DifferentContainers_ModelSwitch_CallsDockerStopStart()
    {
        // Register two models on different containers
        const string containerA = "registered-a";
        const string containerB = "registered-b";
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = containerA, DisplayName = "A", Image = "a:latest",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = containerB, DisplayName = "B", Image = "b:latest",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await _containerRegistry.AddModelMappingAsync(containerA, "model-x");
        await _containerRegistry.AddModelMappingAsync(containerB, "model-y");

        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var worker = CreateWorker(channel);
        var cts = new CancellationTokenSource();
        worker.Start(cts.Token);

        var allDone = new TaskCompletionSource();
        var remaining = 2;

        _inference.InvokeFunc = (req, ct) =>
        {
            if (Interlocked.Decrement(ref remaining) == 0)
                allDone.TrySetResult();
            return Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });
        };

        await channel.Writer.WriteAsync(MakeRequest("model-x", id: "r1"));
        await channel.Writer.WriteAsync(MakeRequest("model-y", id: "r2"));

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Two model starts (different containers → Docker stop + start)
        Assert.Equal(2, _docker.StartedModels.Count);
        Assert.Equal("model-x", _docker.StartedModels[0]);
        Assert.Equal("model-y", _docker.StartedModels[1]);

        // Container was stopped between switches
        Assert.Single(_docker.StoppedContainerIds);

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }

    [Fact]
    public async Task NoContainerRegistry_FallsBackToStandardSwitch()
    {
        // When containerRegistry is null, switching always does Docker stop/start
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var settings = new SchedulerSettings();
        var worker = new SchedulerWorker(
            channel, _docker, _inference, _healthChecker,
            _logStore, _statsTracker, _clock, _logger, settings,
            containerRegistry: null);
        var cts = new CancellationTokenSource();
        worker.Start(cts.Token);

        var allDone = new TaskCompletionSource();
        var remaining = 2;

        _inference.InvokeFunc = (req, ct) =>
        {
            if (Interlocked.Decrement(ref remaining) == 0)
                allDone.TrySetResult();
            return Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });
        };

        await channel.Writer.WriteAsync(MakeRequest("model-a", id: "r1"));
        await channel.Writer.WriteAsync(MakeRequest("model-b", id: "r2"));

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Both models started, container stopped between
        Assert.Equal(2, _docker.StartedModels.Count);
        Assert.Single(_docker.StoppedContainerIds);

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }

    [Fact]
    public async Task ThreeModels_SameContainer_OnlyOneDockerStart()
    {
        const string containerId = "shared-container";
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = containerId, DisplayName = "Shared", Image = "shared:latest",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await _containerRegistry.AddModelMappingAsync(containerId, "m1");
        await _containerRegistry.AddModelMappingAsync(containerId, "m2");
        await _containerRegistry.AddModelMappingAsync(containerId, "m3");

        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var worker = CreateWorker(channel);
        var cts = new CancellationTokenSource();
        worker.Start(cts.Token);

        var allDone = new TaskCompletionSource();
        var remaining = 3;

        _inference.InvokeFunc = (req, ct) =>
        {
            if (Interlocked.Decrement(ref remaining) == 0)
                allDone.TrySetResult();
            return Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });
        };

        await channel.Writer.WriteAsync(MakeRequest("m1", id: "r1"));
        await channel.Writer.WriteAsync(MakeRequest("m2", id: "r2"));
        await channel.Writer.WriteAsync(MakeRequest("m3", id: "r3"));

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Only one Docker start — all three models are on the same container
        Assert.Single(_docker.StartedModels);
        Assert.Empty(_docker.StoppedContainerIds);

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }

    public void Dispose()
    {
    }
}
