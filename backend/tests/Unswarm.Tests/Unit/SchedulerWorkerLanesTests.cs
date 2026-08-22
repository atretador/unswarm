using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services.Scheduler;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Lane-scheduler scenarios on a single target: coexistence without skip budget,
/// skip-budget bypass and exhaustion, QueueStepsTillReset budget reset, unknown-model
/// fail-fast routing, same-lane multi-stream parallelism, and completion-driven wakes.
/// </summary>
public sealed class SchedulerWorkerLanesTests : IDisposable
{
    private readonly FakeInferenceProxy _inference = new();
    private readonly FakeHealthChecker _healthChecker = new();
    private readonly FakeLogStore _logStore = new();
    private readonly FakeStatsTracker _statsTracker = new();
    private readonly FakeClock _clock = new();
    private readonly FakeContainerRegistry _containerRegistry = new();
    private readonly ILogger<SchedulerWorker> _logger = new LoggerFactory().CreateLogger<SchedulerWorker>();

    private Channel<InferenceRequest> _channel = Channel.CreateUnbounded<InferenceRequest>();
    private FakeDockerControllerRouter? _router;
    private SchedulerWorker? _worker;
    private CancellationTokenSource? _cts;

    private SchedulerWorker CreateWorker(SchedulerSettings? settings = null)
    {
        _channel = Channel.CreateUnbounded<InferenceRequest>();
        settings ??= new SchedulerSettings { MaxContainerStartRetries = 1 };
        var host = new FakeDockerController { IdPrefix = "host" };
        _router = new FakeDockerControllerRouter(new Dictionary<string, IDockerController>
        {
            ["host"] = host
        });
        var resolver = new FakeModelTargetResolver();
        resolver.ResolveFunc = (_, _) => Task.FromResult("host");
        _worker = new SchedulerWorker(
            _channel, host, _inference, _healthChecker,
            _logStore, _statsTracker, _clock, _logger, settings,
            _containerRegistry, _router, resolver);
        _cts = new CancellationTokenSource();
        _worker.Start(_cts.Token);
        return _worker;
    }

    private FakeDockerController HostController => (FakeDockerController)_router!.GetController("host");

    private async Task EnqueueAsync(params InferenceRequest[] requests)
    {
        foreach (var request in requests)
            await _channel.Writer.WriteAsync(request);
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
        => new()
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            ModelName = model,
            OriginalJson = "{}",
            Priority = 0,
            EnqueuedAt = DateTimeOffset.UtcNow,
            Tcs = new TaskCompletionSource<InferenceResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
        };

    /// <summary>Registers a runtime with an explicit allow-list and maps a model onto it.</summary>
    private async Task RegisterRuntimeAsync(string id, string image, IReadOnlyList<string> canRunAlongWith, string model, int maxConcurrent = 1)
    {
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = id,
            DisplayName = image,
            Image = image,
            CanRunAlongWith = canRunAlongWith,
            MaxConcurrentInferences = maxConcurrent,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _containerRegistry.AddModelMappingAsync(id, model);
    }

    // ── Scenario helpers ─────────────────────────────────────────────────────

    private async Task RegisterAbcAsync()
    {
        // A ↔ B coexist (symmetric allow-lists); C runs alone.
        await RegisterRuntimeAsync("reg-a", "container-a", ["container-b"], "model-a");
        await RegisterRuntimeAsync("reg-b", "container-b", ["container-a"], "model-b");
        await RegisterRuntimeAsync("reg-c", "container-c", [], "model-c");
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SkipOff_ABCB_BCoexistsWithA_CWaitsForIdle_ThenStopsBoth()
    {
        await RegisterAbcAsync();
        CreateWorker(new SchedulerSettings
        {
            MaxContainerStartRetries = 1,
            EnableParallelSlotSkip = false
        });

        var aStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = new ConcurrentDictionary<string, byte>();
        var cRanWhileBusy = false;

        _inference.InvokeFunc = async (req, ct) =>
        {
            inFlight[req.ModelName] = 0;
            if (req.ModelName == "model-a")
                aStarted.TrySetResult();
            if (inFlight.ContainsKey("model-a") && inFlight.ContainsKey("model-b"))
                bothInFlight.TrySetResult();

            if (req.ModelName == "model-c"
                && (inFlight.ContainsKey("model-a") || inFlight.ContainsKey("model-b")))
            {
                cRanWhileBusy = true;
            }

            if (req.ModelName == "model-a")
            {
                // Held open until the test asserts the coexistence phase.
                await releaseA.Task.WaitAsync(ct);
            }

            inFlight.TryRemove(req.ModelName, out _);
            return new InferenceResponse { StatusCode = 200, TokensGenerated = 1 };
        };

        var rA = MakeRequest("model-a", "rA");
        var rB1 = MakeRequest("model-b", "rB1");
        var rC = MakeRequest("model-c", "rC");
        var rB2 = MakeRequest("model-b", "rB2");

        // A starts; B arrives while A is in flight and must coexist (base rule —
        // no skip budget involved: no blocked head exists yet).
        await EnqueueAsync(rA);
        await aStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await EnqueueAsync(rB1);
        await bothInFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // C (runs-alone) and a second B arrive while A+B are in flight.
        await EnqueueAsync(rC, rB2);

        // C must NOT start while anyone is in flight.
        await Task.Delay(300);
        Assert.DoesNotContain("container-c", HostController.StartedModels);
        Assert.False(rC.Tcs.Task.IsCompleted, "C must wait until A and B are fully idle");

        // Release A → everything drains without any further arrivals.
        releaseA.TrySetResult();

        await Task.WhenAll(rA.Tcs.Task, rB1.Tcs.Task, rC.Tcs.Task, rB2.Tcs.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(cRanWhileBusy, "C must never run while A or B is in flight");

        await Eventually.UntilAsync(() => HostController.StartedModels.Count == 3 && HostController.StoppedContainerIds.Count == 2);
        // Deterministic order: a first, b second (coexisting), c strictly last.
        Assert.Equal(["container-a", "container-b", "container-c"], HostController.StartedModels);

        // C's switch stopped BOTH coexisting containers (tracked target-scoped),
        // and the stop cleared the owner lane's residency so later work re-switches.
        Assert.Equal(2, HostController.StoppedContainerIds.Count);
        Assert.All(HostController.StoppedContainerIds, id => Assert.Contains(id, HostController.StartedContainerIds));

        await ShutdownAsync();
    }

    [Fact]
    public async Task SkipOn_SecondBJumpsOverBlockedC_ExhaustionRestoresFifo_ResetReenablesBypass()
    {
        await RegisterAbcAsync();
        CreateWorker(new SchedulerSettings
        {
            MaxContainerStartRetries = 1,
            EnableParallelSlotSkip = true,
            ParallelSlotSkipLimit = 1,
            QueueStepsTillReset = 2
        });

        var aStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bCompletions = 0;
        var aHeldCount = 0;

        _inference.InvokeFunc = async (req, ct) =>
        {
            if (req.ModelName == "model-a")
            {
                aStarted.TrySetResult();
                // Only the FIRST model-a request is held; later ones flow through.
                if (Interlocked.CompareExchange(ref aHeldCount, 1, 0) == 0)
                    await releaseA.Task.WaitAsync(ct);
            }
            else if (req.ModelName == "model-b")
            {
                Interlocked.Increment(ref bCompletions);
            }

            return new InferenceResponse { StatusCode = 200, TokensGenerated = 1 };
        };

        var rA = MakeRequest("model-a", "rA");
        var rC = MakeRequest("model-c", "rC");
        var rB1 = MakeRequest("model-b", "rB1");

        // A runs (held). C queues behind it and blocks (runs-alone). B arrives:
        // its head bypasses the blocked C head, consuming the skip budget (limit 1).
        await EnqueueAsync(rA);
        await aStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await EnqueueAsync(rC);
        await EnqueueAsync(rB1);
        await rB1.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Budget exhausted (1/1 used). A second B — although compatible with the
        // in-flight A — must NOT jump over C anymore: FIFO is restored.
        var rB2 = MakeRequest("model-b", "rB2");
        await EnqueueAsync(rB2);
        await Task.Delay(300);
        Assert.Equal(1, bCompletions); // B2 has not run
        Assert.DoesNotContain("container-c", HostController.StartedModels);

        // Release A. C (earlier lane) must now run BEFORE B2, and B2 last.
        releaseA.TrySetResult();

        await Task.WhenAll(rA.Tcs.Task, rC.Tcs.Task, rB2.Tcs.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Eventually.UntilAsync(() => HostController.StartedModels.Count == 4 && HostController.StoppedContainerIds.Count == 3);
        // C stopped a+b; B2 found its container stopped+residency cleared by C →
        // fresh start. Strict order: a, b(B1), c, b(B2).
        Assert.Equal(["container-a", "container-b", "container-c", "container-b"], HostController.StartedModels);
        Assert.Equal(3, HostController.StoppedContainerIds.Count);

        // QueueStepsTillReset=2: B1 and B2 completions reset the lane's skip
        // budget. Prove the reset with a fresh bypass opportunity: hold A2, block
        // C2 behind it, then B3 must bypass C2 again (impossible if the budget
        // were still exhausted from B1).
        var a2Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _inference.InvokeFunc = async (req, ct) =>
        {
            if (req.ModelName == "model-a")
            {
                a2Started.TrySetResult();
                await releaseA2.Task.WaitAsync(ct);
            }
            return new InferenceResponse { StatusCode = 200, TokensGenerated = 1 };
        };

        var rA2 = MakeRequest("model-a", "rA2");
        var rC2 = MakeRequest("model-c", "rC2");
        var rB3 = MakeRequest("model-b", "rB3");
        await EnqueueAsync(rA2);
        await a2Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await EnqueueAsync(rC2);
        await EnqueueAsync(rB3);

        // The reset budget lets B3 jump over blocked C2 while A2 is in flight.
        await rB3.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        releaseA2.TrySetResult();
        await Task.WhenAll(rA2.Tcs.Task, rC2.Tcs.Task).WaitAsync(TimeSpan.FromSeconds(5));

        await ShutdownAsync();
    }

    [Fact]
    public async Task UnknownModel_FailsImmediately_NamingMissingRegistration_NothingEnqueued()
    {
        await RegisterRuntimeAsync("reg-a", "container-a", [], "model-a");
        CreateWorker();

        var bad = MakeRequest("no-such-model", "rBad");
        await EnqueueAsync(bad);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bad.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("no-such-model", ex.Message);
        Assert.Contains("not mapped to a registered runtime", ex.Message);

        // Failed at dispatch: nothing reached a lane, no container work happened.
        await Task.Delay(100);
        Assert.Empty(HostController.StartedModels);
        Assert.Empty(_worker!.GetSnapshot().Waiting);
        Assert.Single(_worker.GetSnapshot().RecentCompleted.Where(i => i.Status == QueueItemStatus.Failed));

        await ShutdownAsync();
    }

    [Fact]
    public async Task MultiStream_SameLane_ParallelUpToMaxConcurrentInferences()
    {
        await RegisterRuntimeAsync("reg-m", "container-m", [], "model-m", maxConcurrent: 3);
        CreateWorker();

        var concurrent = 0;
        var maxObserved = 0;
        var threeInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _inference.InvokeFunc = async (req, ct) =>
        {
            var now = Interlocked.Increment(ref concurrent);
            using var _ = new Scope(() => Interlocked.Decrement(ref concurrent));

            if (now > Volatile.Read(ref maxObserved))
                Volatile.Write(ref maxObserved, now);
            if (now >= 3)
                threeInFlight.TrySetResult();

            await release.Task.WaitAsync(ct);
            return new InferenceResponse { StatusCode = 200, TokensGenerated = 1 };
        };

        var requests = Enumerable.Range(1, 4).Select(i => MakeRequest("model-m", $"r{i}")).ToArray();
        await EnqueueAsync(requests);

        // Three run in parallel (MaxConcurrentInferences=3); the fourth must wait.
        await threeInFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        Assert.True(Volatile.Read(ref concurrent) <= 3, "lane must never exceed MaxConcurrentInferences");
        Assert.False(requests[3].Tcs.Task.IsCompleted, "4th request must wait for a free slot");

        release.TrySetResult();
        await Task.WhenAll(requests.Select(r => r.Tcs.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, Volatile.Read(ref maxObserved));
        await Eventually.UntilAsync(() => HostController.StartedModels.Count == 1);
        Assert.Single(HostController.StartedModels); // one container serves all streams

        await ShutdownAsync();
    }

    [Fact]
    public async Task CompletionOfA_WakesWaitingB_WithoutNewArrivals()
    {
        // B runs alone → blocked while A is in flight. Nothing else arrives after
        // B; only A's completion (and its wake) may start B.
        await RegisterRuntimeAsync("reg-a", "container-a", [], "model-a");
        await RegisterRuntimeAsync("reg-b", "container-b", [], "model-b");
        CreateWorker();

        var aStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _inference.InvokeFunc = async (req, ct) =>
        {
            if (req.ModelName == "model-a")
            {
                aStarted.TrySetResult();
                await releaseA.Task.WaitAsync(ct);
            }
            return new InferenceResponse { StatusCode = 200, TokensGenerated = 1 };
        };

        var rA = MakeRequest("model-a", "rA");
        var rB = MakeRequest("model-b", "rB");
        await EnqueueAsync(rA);
        await aStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await EnqueueAsync(rB);

        await Task.Delay(300);
        Assert.False(rB.Tcs.Task.IsCompleted, "B must wait while A runs");
        Assert.DoesNotContain("container-b", HostController.StartedModels);

        // Sole scheduling event: A completes → wake → B starts. No new arrivals.
        releaseA.TrySetResult();

        await rB.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Eventually.UntilAsync(() => HostController.StartedModels.Count == 2 && HostController.StoppedContainerIds.Count == 1);
        Assert.Equal(["container-a", "container-b"], HostController.StartedModels);
        Assert.Single(HostController.StoppedContainerIds); // B's switch stopped A's container

        await ShutdownAsync();
    }

    public void Dispose()
    {
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
