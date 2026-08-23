using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services.Scheduler;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Conversation-affinity scenarios: consecutive tool-call-loop requests sharing a
/// ConversationKey hold their runtime against eviction for a dwell window; an
/// incompatible pending model must wait (no container stop) until the dwell
/// expires. Covers hold refresh, affinity-off passthrough, HeldByConversation
/// snapshot mapping, and RequestCount accumulation.
/// </summary>
public sealed class SchedulerConversationAffinityTests : IDisposable
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

    private SchedulerWorker CreateWorker(SchedulerSettings settings)
    {
        _channel = Channel.CreateUnbounded<InferenceRequest>();
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

    private static InferenceRequest MakeRequest(string model, string? conversationKey = null, string? id = null)
        => new()
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            ModelName = model,
            OriginalJson = "{}",
            Priority = 0,
            EnqueuedAt = DateTimeOffset.UtcNow,
            Tcs = new TaskCompletionSource<InferenceResponse>(TaskCreationOptions.RunContinuationsAsynchronously),
            ConversationKey = conversationKey
        };

    /// <summary>Registers two runs-alone runtimes (mutually incompatible) with one model each.</summary>
    private async Task RegisterIncompatiblePairAsync()
    {
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-a",
            DisplayName = "container-a",
            Image = "container-a",
            CanRunAlongWith = [],
            MaxConcurrentInferences = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _containerRegistry.AddModelMappingAsync("reg-a", "model-a");

        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-b",
            DisplayName = "container-b",
            Image = "container-b",
            CanRunAlongWith = [],
            MaxConcurrentInferences = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _containerRegistry.AddModelMappingAsync("reg-b", "model-b");
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AffinityOn_HotConversationHoldsRuntime_BBlocksUntilDwellExpires()
    {
        await RegisterIncompatiblePairAsync();
        CreateWorker(new SchedulerSettings
        {
            MaxContainerStartRetries = 1,
            EnableConversationAffinity = true,
            ConversationDwellSeconds = 2
        });

        _inference.InvokeFunc = (_, _) =>
            Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });

        // A completes on runtime-a, creating a hot conversation.
        var rA = MakeRequest("model-a", "conv:t1", "rA");
        await EnqueueAsync(rA);
        await rA.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Eventually.UntilAsync(() => HostController.StartedModels.Contains("container-a"));

        // Incompatible B arrives within the dwell window → must stay WAITING and
        // container-a must NOT be stopped (no eviction start between tool calls).
        var rB = MakeRequest("model-b", id: "rB");
        await EnqueueAsync(rB);

        await Task.Delay(600);
        Assert.False(rB.Tcs.Task.IsCompleted, "B must wait while the conversation holds runtime-a");
        Assert.DoesNotContain("container-b", HostController.StartedModels);
        Assert.Empty(HostController.StoppedContainerIds);

        // After the dwell expires the hold lifts: B starts and evicts container-a.
        await rB.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Eventually.UntilAsync(() =>
            HostController.StartedModels.Contains("container-b")
            && HostController.StoppedContainerIds.Count == 1);

        await ShutdownAsync();
    }

    [Fact]
    public async Task AffinityOn_NewConversationRequestExtendsHold()
    {
        await RegisterIncompatiblePairAsync();
        CreateWorker(new SchedulerSettings
        {
            MaxContainerStartRetries = 1,
            EnableConversationAffinity = true,
            ConversationDwellSeconds = 2
        });

        _inference.InvokeFunc = (_, _) =>
            Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });

        // First A-conversation iteration completes at t0.
        var rA1 = MakeRequest("model-a", "conv:t1", "rA1");
        await EnqueueAsync(rA1);
        await rA1.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // B arrives and is held.
        var rB = MakeRequest("model-b", id: "rB");
        await EnqueueAsync(rB);
        await Task.Delay(300);

        // Second iteration of the SAME conversation completes at ~t0+0.3s —
        // refreshing LastSeen past the original dwell boundary (t0+2s).
        var rA2 = MakeRequest("model-a", "conv:t1", "rA2");
        await EnqueueAsync(rA2);
        await rA2.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // At ~t0+1.8s the ORIGINAL dwell has expired but the refreshed hold has
        // not — B must still be waiting and container-a still up.
        await Task.Delay(1500);
        Assert.False(rB.Tcs.Task.IsCompleted, "refreshed hold must keep B waiting past the original dwell");
        Assert.DoesNotContain("container-b", HostController.StartedModels);
        Assert.Empty(HostController.StoppedContainerIds);

        // After the quiet period B proceeds and evicts container-a.
        await rB.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Eventually.UntilAsync(() =>
            HostController.StartedModels.Contains("container-b")
            && HostController.StoppedContainerIds.Count == 1);

        await ShutdownAsync();
    }

    [Fact]
    public async Task AffinityOff_IncompatibleModelEvictsImmediatelyAfterCompletion()
    {
        await RegisterIncompatiblePairAsync();
        CreateWorker(new SchedulerSettings
        {
            MaxContainerStartRetries = 1,
            EnableConversationAffinity = false,
            ConversationDwellSeconds = 60
        });

        _inference.InvokeFunc = (_, _) =>
            Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });

        var rA = MakeRequest("model-a", "conv:t1", "rA");
        await EnqueueAsync(rA);
        await rA.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Eventually.UntilAsync(() => HostController.StartedModels.Contains("container-a"));

        // Affinity OFF: existing behavior — B evicts right after A completes even
        // though the conversation key is well inside the (long) dwell window.
        var rB = MakeRequest("model-b", id: "rB");
        await EnqueueAsync(rB);

        await rB.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Eventually.UntilAsync(() =>
            HostController.StartedModels.Contains("container-b")
            && HostController.StoppedContainerIds.Count == 1);

        await ShutdownAsync();
    }

    [Fact]
    public async Task HeldByConversation_MappedForHotConversationHold_NullForInFlightBlock()
    {
        await RegisterIncompatiblePairAsync();
        CreateWorker(new SchedulerSettings
        {
            MaxContainerStartRetries = 1,
            EnableConversationAffinity = true,
            ConversationDwellSeconds = 60
        });

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

        // Phase 1: A in flight blocks B — plain in-flight work, NOT a hold.
        var rA = MakeRequest("model-a", "conv:t1", "rA");
        await EnqueueAsync(rA);
        await aStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var rB = MakeRequest("model-b", id: "rB");
        await EnqueueAsync(rB);

        await Eventually.UntilAsync(() =>
            _worker!.GetSnapshot().Waiting.Any(w => w.Id == "rB" && w.BlockedByRuntimeIds.Contains("reg-a")));
        var inFlightBlocked = _worker!.GetSnapshot().Waiting.Single(w => w.Id == "rB");
        Assert.Null(inFlightBlocked.HeldByConversation);

        // Phase 2: A completes → hot conversation holds runtime-a for B.
        releaseA.TrySetResult();
        await rA.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Eventually.UntilAsync(() =>
        {
            var item = _worker!.GetSnapshot().Waiting.FirstOrDefault(w => w.Id == "rB");
            return item?.HeldByConversation is not null;
        });

        var held = _worker.GetSnapshot().Waiting.Single(w => w.Id == "rB");
        Assert.Equal("reg-a", held.HeldByConversation!.RuntimeId);
        Assert.Equal("model-a", held.HeldByConversation.Model);
        Assert.Equal(1, held.HeldByConversation.RequestCount);

        // Hold expiry = last conversation activity + dwell window (60s here), so
        // it must lie in the future but within roughly one dwell of now.
        var expiresAt = held.HeldByConversation.HoldExpiresAt;
        Assert.True(expiresAt > DateTimeOffset.UtcNow,
            "hold must not have expired yet (dwell is 60s)");
        Assert.True(expiresAt <= DateTimeOffset.UtcNow.AddSeconds(61),
            "hold expiry must be lastSeen + dwell seconds");

        await ShutdownAsync();
    }

    [Fact]
    public async Task ReleaseHolds_ClearsHold_BStartsPromptlyWithoutDwellExpiry()
    {
        await RegisterIncompatiblePairAsync();
        CreateWorker(new SchedulerSettings
        {
            MaxContainerStartRetries = 1,
            EnableConversationAffinity = true,
            ConversationDwellSeconds = 60 // far out — only the release can lift the hold
        });

        _inference.InvokeFunc = (_, _) =>
            Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });

        // A completes on runtime-a; B arrives and is held by the hot conversation.
        var rA = MakeRequest("model-a", "conv:t1", "rA");
        await EnqueueAsync(rA);
        await rA.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Eventually.UntilAsync(() => HostController.StartedModels.Contains("container-a"));

        var rB = MakeRequest("model-b", id: "rB");
        await EnqueueAsync(rB);

        await Eventually.UntilAsync(() =>
        {
            var item = _worker!.GetSnapshot().Waiting.FirstOrDefault(w => w.Id == "rB");
            return item?.HeldByConversation is not null;
        });

        // Unknown target id → no-op false (controller maps this to 404).
        Assert.False(_worker!.ReleaseConversationHolds("no-such-target"));

        // Release: B starts promptly despite the 60s dwell being nowhere near
        // expired; container-a is stopped only by B's normal incompatible switch.
        Assert.True(_worker.ReleaseConversationHolds("host"));

        await rB.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Eventually.UntilAsync(() =>
            HostController.StartedModels.Contains("container-b")
            && HostController.StoppedContainerIds.Count == 1);

        await ShutdownAsync();
    }

    [Fact]
    public async Task RequestCount_IncrementsAcrossCompletionsOfSameConversationKey()
    {
        await RegisterIncompatiblePairAsync();
        CreateWorker(new SchedulerSettings
        {
            MaxContainerStartRetries = 1,
            EnableConversationAffinity = true,
            ConversationDwellSeconds = 60
        });

        _inference.InvokeFunc = (_, _) =>
            Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });

        // Three consecutive tool-loop iterations of the same conversation.
        for (var i = 1; i <= 3; i++)
        {
            var rA = MakeRequest("model-a", "conv:t1", $"rA{i}");
            await EnqueueAsync(rA);
            await rA.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        // A waiting incompatible item exposes the accumulated request count.
        var rB = MakeRequest("model-b", id: "rB");
        await EnqueueAsync(rB);

        await Eventually.UntilAsync(() =>
        {
            var item = _worker!.GetSnapshot().Waiting.FirstOrDefault(w => w.Id == "rB");
            return item?.HeldByConversation?.RequestCount == 3;
        });

        var held = _worker.GetSnapshot().Waiting.Single(w => w.Id == "rB");
        Assert.Equal("reg-a", held.HeldByConversation!.RuntimeId);
        Assert.Equal("model-a", held.HeldByConversation.Model);

        await ShutdownAsync();
    }

    public void Dispose()
    {
    }
}
