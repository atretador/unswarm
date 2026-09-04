using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services.Scheduler;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Shutdown-drain semantics: when the scheduler's stopping token fires, EVERY
/// queued request — whether still in the global channel or already dispatched to
/// a per-target channel — must have its Tcs completed promptly so awaiting HTTP
/// handlers return instead of hanging until client timeout.
/// </summary>
public sealed class SchedulerWorkerShutdownTests
{
    private readonly FakeDockerController _docker = new();
    private readonly FakeInferenceProxy _inference = new();
    private readonly FakeHealthChecker _healthChecker = new();
    private readonly FakeLogStore _logStore = new();
    private readonly FakeStatsTracker _statsTracker = new();
    private readonly FakeClock _clock = new();
    private readonly FakeContainerRegistry _containerRegistry = new();
    private readonly ILogger<SchedulerWorker> _logger = new LoggerFactory().CreateLogger<SchedulerWorker>();

    public SchedulerWorkerShutdownTests()
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
        SchedulerSettings? settings = null,
        FakeDockerControllerRouter? router = null,
        FakeModelTargetResolver? resolver = null)
    {
        channel ??= Channel.CreateUnbounded<InferenceRequest>();
        settings ??= new SchedulerSettings { MaxContainerStartRetries = 1 };
        return new SchedulerWorker(channel, _docker, _inference, _healthChecker,
            _logStore, _statsTracker, _clock, _logger, settings,
            Options.Create(new ContainerHostOptions()), _containerRegistry, router, resolver);
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
    public async Task Shutdown_CompletesRequestsQueuedInTargetChannel()
    {
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var worker = CreateWorker(channel);
        var cts = new CancellationTokenSource();

        // First request blocks in inference until released — keeps the target
        // worker busy so later requests pile up in the per-target channel.
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _inference.InvokeFunc = async (req, ct) =>
        {
            firstStarted.TrySetResult();
            await releaseFirst.Task.WaitAsync(ct);
            return new InferenceResponse { StatusCode = 200, TokensGenerated = 1 };
        };

        worker.Start(cts.Token);

        var r1 = MakeRequest("llama", "r1");
        await channel.Writer.WriteAsync(r1);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // These sit in the per-target channel while r1 blocks.
        var queued = Enumerable.Range(2, 4).Select(i => MakeRequest("llama", $"r{i}")).ToList();
        foreach (var req in queued)
            await channel.Writer.WriteAsync(req);
        await Task.Delay(200); // let the dispatcher forward them to the target slot

        cts.Cancel();

        // Every queued request must resolve promptly (cancelled) on shutdown.
        foreach (var req in queued)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => req.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        await worker.WaitForShutdownAsync();
    }

    [Fact]
    public async Task Shutdown_CompletesRequestsStillInGlobalChannel()
    {
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var resolver = new FakeModelTargetResolver();
        // Resolver hangs until cancellation — dispatcher stalls on the first
        // request, leaving the rest in the GLOBAL channel undispatched.
        resolver.ResolveFunc = async (_, tok) =>
        {
            await Task.Delay(Timeout.Infinite, tok);
            return ExecutionTarget.HostId;
        };
        var worker = CreateWorker(channel, resolver: resolver);
        var cts = new CancellationTokenSource();

        worker.Start(cts.Token);

        var requests = Enumerable.Range(1, 3).Select(i => MakeRequest("llama", $"g{i}")).ToList();
        foreach (var req in requests)
            await channel.Writer.WriteAsync(req);
        await Task.Delay(200); // dispatcher picks up g1 and blocks inside ResolveAsync

        cts.Cancel();

        foreach (var req in requests)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => req.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        await worker.WaitForShutdownAsync();
    }

    [Fact]
    public async Task WaitingItemsBehindModelSwitch_AllResolve_NeverMarkedProcessing()
    {
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var settings = new SchedulerSettings { LazyStop = true, BatchDrain = true, MaxContainerStartRetries = 1 };
        var worker = CreateWorker(channel, settings: settings);
        var cts = new CancellationTokenSource();

        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _inference.InvokeFunc = async (req, ct) =>
        {
            if (req.ModelName == "llama" && req.Id == "r1")
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(ct);
            }
            return new InferenceResponse { StatusCode = 200, TokensGenerated = 1 };
        };

        worker.Start(cts.Token);

        // Channel order: r1 (llama, blocks) → mistral → r2/r3 (llama).
        var r1 = MakeRequest("llama", "r1");
        var mistral = MakeRequest("mistral", "m1");
        var r2 = MakeRequest("llama", "r2");
        var r3 = MakeRequest("llama", "r3");
        await channel.Writer.WriteAsync(r1);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await channel.Writer.WriteAsync(mistral);
        await channel.Writer.WriteAsync(r2);
        await channel.Writer.WriteAsync(r3);

        releaseFirst.TrySetResult();

        // Lane scheduling guarantees every queued item RESOLVES — none may hang
        // forever on its Tcs nor be flipped to Processing while still waiting.
        var result1 = await r1.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, result1.StatusCode);
        var resultM = await mistral.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, resultM.StatusCode);
        var result2 = await r2.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, result2.StatusCode);
        var result3 = await r3.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, result3.StatusCode);

        await Eventually.UntilAsync(() => _docker.StartedModels.Count == 2 && _docker.StoppedContainerIds.Count == 1);
        // The mistral switch stopped the llama container exactly once.
        Assert.Equal(["llama", "mistral"], _docker.StartedModels);
        Assert.Single(_docker.StoppedContainerIds);

        // Nothing is stuck in Processing after the dust settles.
        await Task.Delay(100);
        Assert.Empty(worker.GetSnapshot().Processing);

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }
}
