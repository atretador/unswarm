using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using Unswarm.Core.Services.Remote;
using Unswarm.Core.Services.Scheduler;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// SwitchModelLockedAsync branch coverage beyond the retry/health-failure basics:
/// script-runtime start paths (host + agent, success and failure), RuntimeContainerId
/// persistence failure tolerance, ConcurrencyGate replacement when idle, instant
/// switches within one registered runtime, and StopIncompatibleContainersAsync
/// reconcile of orphaned running containers.
/// </summary>
public sealed class SchedulerWorkerSwitchScriptTests : IDisposable
{
    private readonly FakeInferenceProxy _inference = new();
    private readonly FakeHealthChecker _healthChecker = new();
    private readonly FakeLogStore _logStore = new();
    private readonly FakeStatsTracker _statsTracker = new();
    private readonly FakeClock _clock = new();
    private readonly FakeContainerRegistry _containerRegistry = new();
    private readonly FakeAgentRegistry _agentRegistry = new();
    private readonly ILogger<SchedulerWorker> _logger = new LoggerFactory().CreateLogger<SchedulerWorker>();
    private readonly ILogger<HostScriptRuntimeController> _scriptLogger =
        new LoggerFactory().CreateLogger<HostScriptRuntimeController>();

    private Channel<InferenceRequest> _channel = Channel.CreateUnbounded<InferenceRequest>();
    private SchedulerWorker? _worker;
    private CancellationTokenSource? _cts;

    private SchedulerWorker CreateWorker(
        IDockerControllerRouter router,
        IModelTargetResolver resolver,
        HostScriptRuntimeController? scriptController = null,
        IContainerRegistry? containerRegistry = null,
        SchedulerSettings? settings = null)
    {
        _channel = Channel.CreateUnbounded<InferenceRequest>();
        settings ??= new SchedulerSettings { MaxContainerStartRetries = 1 };
        var host = (FakeDockerController)router.GetController("host");
        _worker = new SchedulerWorker(
            _channel, host, _inference, _healthChecker,
            _logStore, _statsTracker, _clock, _logger, settings,
            containerRegistry ?? _containerRegistry, router, resolver,
            scriptController: scriptController,
            agentRegistry: _agentRegistry);
        _cts = new CancellationTokenSource();
        _worker.Start(_cts.Token);
        return _worker;
    }

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

    private Task RegisterModelAsync(string model, string image, Func<RegisteredRuntime, RegisteredRuntime>? configure = null)
        => RegisterRuntimeAsync($"reg-{model}", image, [model], configure);

    private async Task RegisterRuntimeAsync(
        string regId, string image, IReadOnlyList<string> modelIds,
        Func<RegisteredRuntime, RegisteredRuntime>? configure = null)
    {
        var runtime = new RegisteredRuntime
        {
            Id = regId,
            DisplayName = image,
            Image = image,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        if (configure is not null)
            runtime = configure(runtime);
        await _containerRegistry.CreateAsync(runtime);
        foreach (var modelId in modelIds)
            await _containerRegistry.AddModelMappingAsync(regId, modelId);
    }

    private static string WriteLauncherScript(string tempDir, string fileName, string contents)
    {
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    private bool HasLog(Func<string, bool> predicate) => _logStore.Entries.Any(e => predicate(e.Message));

    // ── Host script runtime ───────────────────────────────────────────────────

    [Fact]
    public async Task HostScript_StartSuccess_CompletesSwitch_AndInstantSwitchesAgain()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "unswarm-tests-" + Guid.NewGuid().ToString("N"));
        var launcher = WriteLauncherScript(tempDir, "launcher.sh", "#!/bin/bash\nsleep 30\n");
        var scriptController = new HostScriptRuntimeController(_scriptLogger, tempDir);
        try
        {
            await RegisterRuntimeAsync("reg-script-model", "script-img", ["script-model-a", "script-model-b"], r => r with
            {
                RuntimeKind = RuntimeKind.Script,
                LauncherPath = launcher,
                ContainerPort = 9377
            });

            var router = new FakeDockerControllerRouter(new Dictionary<string, IDockerController>
            {
                ["host"] = new FakeDockerController { IdPrefix = "host" }
            });
            var resolver = new FakeModelTargetResolver();
            resolver.ResolveFunc = (_, _) => Task.FromResult("host");
            CreateWorker(router, resolver, scriptController: scriptController);

            var r1 = MakeRequest("script-model-a", "r1");
            await EnqueueAsync(r1);
            var result1 = await r1.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(200, result1.StatusCode);

            // Health was waited on the declared script port; completion logged.
            Assert.Contains(9377, _healthChecker.CheckedPorts);
            await Eventually.UntilAsync(() => HasLog(m => m.Contains("Script switch complete")));

            // No docker container was started for a script runtime.
            var host = (FakeDockerController)router.GetController("host");
            Assert.Empty(host.StartedModels);

            // Second model on the SAME runtime → instant switch (container already tracked).
            var r2 = MakeRequest("script-model-b", "r2");
            await EnqueueAsync(r2);
            var result2 = await r2.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(200, result2.StatusCode);
            await Eventually.UntilAsync(() => HasLog(m => m.Contains("Instant switch")));
        }
        finally
        {
            await scriptController.StopScriptAsync("reg-script-model");
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            await ShutdownAsync();
        }
    }

    [Fact]
    public async Task HostScript_MissingLauncher_FailsRequestAndLogsError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "unswarm-tests-" + Guid.NewGuid().ToString("N"));
        var scriptController = new HostScriptRuntimeController(_scriptLogger, tempDir);
        try
        {
            await RegisterModelAsync("script-broken", "script-broken-img", r => r with
            {
                RuntimeKind = RuntimeKind.Script,
                LauncherPath = Path.Combine(tempDir, "does-not-exist.sh"),
                ContainerPort = 9378
            });

            var router = new FakeDockerControllerRouter(new Dictionary<string, IDockerController>
            {
                ["host"] = new FakeDockerController { IdPrefix = "host" }
            });
            var resolver = new FakeModelTargetResolver();
            resolver.ResolveFunc = (_, _) => Task.FromResult("host");
            CreateWorker(router, resolver, scriptController: scriptController);

            var req = MakeRequest("script-broken", "r1");
            await EnqueueAsync(req);

            // Script start returned an ErrorMessage → switch returns without setting
            // residency → the lane runner fails the request as "not available".
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => req.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("not available", ex.Message);

            await Eventually.UntilAsync(() =>
                HasLog(m => m.Contains("Script start failed") && m.Contains("does-not-exist.sh")));
            Assert.True(HasLog(m => m.Contains("Launcher script not found")));

            // The failure is terminal and recorded with a reason.
            await Eventually.UntilAsync(() =>
                _worker!.GetSnapshot().RecentCompleted.Any(i =>
                    i.Id == "r1" && i.Status == QueueItemStatus.Failed));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            await ShutdownAsync();
        }
    }

    [Fact]
    public async Task HostScript_NoControllerAvailable_FailsGracefullyWithoutCrash()
    {
        await RegisterModelAsync("script-noctrl", "script-noctrl-img", r => r with
        {
            RuntimeKind = RuntimeKind.Script,
            LauncherPath = "/tmp/unswarm-nonexistent-launcher.sh",
            ContainerPort = 9379
        });

        var router = new FakeDockerControllerRouter(new Dictionary<string, IDockerController>
        {
            ["host"] = new FakeDockerController { IdPrefix = "host" }
        });
        var resolver = new FakeModelTargetResolver();
        resolver.ResolveFunc = (_, _) => Task.FromResult("host");
        CreateWorker(router, resolver, scriptController: null);

        var req = MakeRequest("script-noctrl", "r1");
        await EnqueueAsync(req);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => req.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("not available", ex.Message);

        await Eventually.UntilAsync(() =>
            HasLog(m => m.Contains("HostScriptRuntimeController not available")));
        await Eventually.UntilAsync(() =>
            _worker!.GetSnapshot().RecentCompleted.Any(i =>
                i.Id == "r1" && i.Status == QueueItemStatus.Failed));

        await ShutdownAsync();
    }

    [Fact]
    public async Task HostScript_HealthWaitFails_RequestFaultsWithTimeoutError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "unswarm-tests-" + Guid.NewGuid().ToString("N"));
        var launcher = WriteLauncherScript(tempDir, "launcher-sick.sh", "#!/bin/bash\nsleep 30\n");
        var scriptController = new HostScriptRuntimeController(_scriptLogger, tempDir);
        try
        {
            await RegisterModelAsync("script-sick", "script-sick-img", r => r with
            {
                RuntimeKind = RuntimeKind.Script,
                LauncherPath = launcher,
                ContainerPort = 9380
            });

            var router = new FakeDockerControllerRouter(new Dictionary<string, IDockerController>
            {
                ["host"] = new FakeDockerController { IdPrefix = "host" }
            });
            var resolver = new FakeModelTargetResolver();
            resolver.ResolveFunc = (_, _) => Task.FromResult("host");
            CreateWorker(router, resolver, scriptController: scriptController);
            _healthChecker.IsReady = false; // WaitForReadyAsync throws TimeoutException

            var req = MakeRequest("script-sick", "r1");
            await EnqueueAsync(req);

            var ex = await Assert.ThrowsAnyAsync<Exception>(
                () => req.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsType<TimeoutException>(ex);

            // The script WAS started; the health gate then failed the switch. The
            // switch-level failure goes to ILogger; the persisted failure reason
            // lands on the queue item.
            await Eventually.UntilAsync(() =>
                _worker!.GetSnapshot().RecentCompleted.Any(i =>
                    i.Id == "r1" && i.Status == QueueItemStatus.Failed));
            var failed = _worker!.GetSnapshot().RecentCompleted.First(i => i.Id == "r1");
            Assert.Contains("Health check timeout", failed.ErrorMessage);
        }
        finally
        {
            await scriptController.StopScriptAsync("reg-script-sick");
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            await ShutdownAsync();
        }
    }

    // ── Agent script runtime ──────────────────────────────────────────────────

    private static AgentMessage MakeReply(string commandId, object payload)
        => new()
        {
            Type = RemoteAgentDockerController.CommandResultType,
            Id = commandId,
            Agent = "gpu1",
            Payload = System.Text.Json.JsonSerializer.SerializeToElement(payload,
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase })
        };

    [Fact]
    public async Task AgentScript_StartSuccess_ViaRemoteController()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "unswarm-tests-" + Guid.NewGuid().ToString("N"));
        var scriptController = new HostScriptRuntimeController(_scriptLogger, tempDir);
        try
        {
            _agentRegistry.Register("gpu1", new AgentConnection
            {
                Name = "gpu1", ConnectionId = "cid-1", IsConnected = true
            }, new FakeWebSocket());

            var remote = new RemoteAgentDockerController("gpu1", _agentRegistry,
                new LoggerFactory().CreateLogger<RemoteAgentDockerController>());
            _agentRegistry.OnSend = msg =>
            {
                remote.HandleIncomingMessage(MakeReply(msg.Id!, new { ok = true, pid = 4242 }));
                return Task.FromResult<AgentMessage?>(null);
            };

            await RegisterRuntimeAsync("reg-agent-script-model", "agent-script-img",
                ["agent-script-a", "agent-script-b"], r => r with
            {
                RuntimeKind = RuntimeKind.Script,
                Agent = "gpu1",
                // The agent path validates LauncherPath presence even though the file
                // lives on the agent, not on the host.
                LauncherPath = "/opt/scripts/agent-script.sh",
                ContainerPort = 9381
            });

            var router = new FakeDockerControllerRouter(new Dictionary<string, IDockerController>
            {
                ["host"] = new FakeDockerController { IdPrefix = "host" },
                ["agent:gpu1"] = remote
            });
            var resolver = new FakeModelTargetResolver();
            resolver.ResolveFunc = (_, _) => Task.FromResult("agent:gpu1");
            CreateWorker(router, resolver, scriptController: scriptController);

            var req = MakeRequest("agent-script-a", "r1");
            await EnqueueAsync(req);

            var result = await req.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(200, result.StatusCode);

            // Health waited on the agent target using the agent name as host fallback.
            Assert.Contains(9381, _healthChecker.CheckedPorts);
            await Eventually.UntilAsync(() =>
                HasLog(m => m.Contains("Script switch complete on agent:gpu1")));

            // A second model on the same runtime hits the instant-switch path.
            var req2 = MakeRequest("agent-script-b", "r2");
            await EnqueueAsync(req2);
            var result2 = await req2.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(200, result2.StatusCode);
            await Eventually.UntilAsync(() => HasLog(m => m.Contains("Instant switch")));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            await ShutdownAsync();
        }
    }

    [Fact]
    public async Task AgentScript_ControllerNotRemote_FailsFastWithClearError()
    {
        await RegisterModelAsync("agent-script-bad", "agent-script-bad-img", r => r with
        {
            RuntimeKind = RuntimeKind.Script,
            Agent = "gpu1",
            LauncherPath = "/opt/scripts/agent-script-bad.sh",
            ContainerPort = 9382
        });

        // Agent target routed to a plain docker controller — not a RemoteAgentDockerController.
        var router = new FakeDockerControllerRouter(new Dictionary<string, IDockerController>
        {
            ["host"] = new FakeDockerController { IdPrefix = "host" },
            ["agent:gpu1"] = new FakeDockerController { IdPrefix = "plain-agent" }
        });
        var resolver = new FakeModelTargetResolver();
        resolver.ResolveFunc = (_, _) => Task.FromResult("agent:gpu1");
        CreateWorker(router, resolver);

        var req = MakeRequest("agent-script-bad", "r1");
        await EnqueueAsync(req);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => req.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("not available", ex.Message);

        await Eventually.UntilAsync(() =>
            HasLog(m => m.Contains("does not have a connected RemoteAgentDockerController")));
        await Eventually.UntilAsync(() =>
            _worker!.GetSnapshot().RecentCompleted.Any(i =>
                i.Id == "r1" && i.Status == QueueItemStatus.Failed));

        await ShutdownAsync();
    }

    // ── Registry persistence + concurrency gate + reconcile branches ─────────

    /// <summary>Registry wrapper whose UpdateAsync always throws (persistence failure).</summary>
    private sealed class ThrowingUpdateRegistry : IContainerRegistry
    {
        private readonly FakeContainerRegistry _inner;
        public ThrowingUpdateRegistry(FakeContainerRegistry inner) => _inner = inner;

        public Task<IReadOnlyList<RegisteredRuntime>> ListAllAsync(CancellationToken ct = default)
            => _inner.ListAllAsync(ct);
        public Task<RegisteredRuntime?> GetAsync(string id, CancellationToken ct = default)
            => _inner.GetAsync(id, ct);
        public Task<RegisteredRuntime> CreateAsync(RegisteredRuntime container, CancellationToken ct = default)
            => _inner.CreateAsync(container, ct);
        public Task<RegisteredRuntime> UpdateAsync(string id, RegisteredRuntime container, CancellationToken ct = default)
            => throw new InvalidOperationException("registry write failed");
        public Task DeleteAsync(string id, CancellationToken ct = default)
            => _inner.DeleteAsync(id, ct);
        public Task AddModelMappingAsync(string registeredContainerId, string modelId, CancellationToken ct = default)
            => _inner.AddModelMappingAsync(registeredContainerId, modelId, ct);
        public Task RemoveModelMappingAsync(string registeredContainerId, string modelId, CancellationToken ct = default)
            => _inner.RemoveModelMappingAsync(registeredContainerId, modelId, ct);
        public Task<IReadOnlyList<string>> GetModelIdsForContainerAsync(string registeredContainerId, CancellationToken ct = default)
            => _inner.GetModelIdsForContainerAsync(registeredContainerId, ct);
        public Task<string?> GetContainerIdForModelAsync(string modelName, CancellationToken ct = default)
            => _inner.GetContainerIdForModelAsync(modelName, ct);
        public Task<(RegisteredRuntime A, RegisteredRuntime B)?> UpdateConcurrencyPairAsync(
            string idA, IReadOnlyList<string> newCanRunAlongWithA,
            string idB, IReadOnlyList<string> newCanRunAlongWithB, CancellationToken ct = default)
            => _inner.UpdateConcurrencyPairAsync(idA, newCanRunAlongWithA, idB, newCanRunAlongWithB, ct);
    }

    [Fact]
    public async Task RegistryUpdateFails_AfterContainerStart_SwitchStillCompletes()
    {
        await RegisterModelAsync("persist-model", "persist-image");

        var router = new FakeDockerControllerRouter(new Dictionary<string, IDockerController>
        {
            ["host"] = new FakeDockerController { IdPrefix = "host" }
        });
        var resolver = new FakeModelTargetResolver();
        resolver.ResolveFunc = (_, _) => Task.FromResult("host");
        CreateWorker(router, resolver, containerRegistry: new ThrowingUpdateRegistry(_containerRegistry));

        var req = MakeRequest("persist-model", "r1");
        await EnqueueAsync(req);

        // The docker container started fine; the RuntimeContainerId persistence write
        // failed but must NOT fail the switch or crash the worker.
        var result = await req.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(200, result.StatusCode);

        var host = (FakeDockerController)router.GetController("host");
        await Eventually.UntilAsync(() => host.StartedModels.Count == 1);
        Assert.Single(host.StartedContainerIds);

        // A follow-up request still works (worker healthy after the swallowed error).
        var req2 = MakeRequest("persist-model", "r2");
        await EnqueueAsync(req2);
        var result2 = await req2.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(200, result2.StatusCode);

        await ShutdownAsync();
    }

    [Fact]
    public async Task ConcurrencyGate_RebuiltWhenIdle_NewMaxConcurrencyAllowsParallelHeads()
    {
        // One runtime serving two models with MaxConcurrentInferences=2. The first
        // request establishes residency; the second model's switch runs while the
        // lane is idle → ConcurrencyGate replaced with the new limit → two heads.
        await RegisterRuntimeAsync("reg-multi", "multi-image", ["m1", "m2"], r => r with
        {
            MaxConcurrentInferences = 2
        });

        var router = new FakeDockerControllerRouter(new Dictionary<string, IDockerController>
        {
            ["host"] = new FakeDockerController { IdPrefix = "host" }
        });
        var resolver = new FakeModelTargetResolver();
        resolver.ResolveFunc = (_, _) => Task.FromResult("host");
        CreateWorker(router, resolver);

        var bothInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = new HashSet<string>();
        object gate = new();
        _inference.InvokeFunc = (req, ct) =>
        {
            lock (gate)
            {
                inFlight.Add(req.Id);
                if (inFlight.Count >= 2)
                    bothInFlight.TrySetResult();
            }
            return Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });
        };

        var r1 = MakeRequest("m1", "r1");
        await EnqueueAsync(r1);
        await r1.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Two requests for the other model of the SAME runtime: the first triggers
        // the (instant) switch while ActiveInferences==0 → gate rebuilt to 2 slots.
        var r2 = MakeRequest("m2", "r2");
        var r3 = MakeRequest("m2", "r3");
        await EnqueueAsync(r2, r3);

        await bothInFlight.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(r2.Tcs.Task, r3.Tcs.Task).WaitAsync(TimeSpan.FromSeconds(10));

        // Only ONE container start ever happened (multi-model runtime, no churn).
        var host = (FakeDockerController)router.GetController("host");
        await Eventually.UntilAsync(() => host.StartedModels.Count == 1);
        Assert.Empty(host.StoppedContainerIds);

        await ShutdownAsync();
    }

    [Fact]
    public async Task InstantSwitch_SecondModelSameRuntime_NoContainerChurn()
    {
        await RegisterRuntimeAsync("reg-instant", "instant-image", ["ia", "ib"]);

        var router = new FakeDockerControllerRouter(new Dictionary<string, IDockerController>
        {
            ["host"] = new FakeDockerController { IdPrefix = "host" }
        });
        var resolver = new FakeModelTargetResolver();
        resolver.ResolveFunc = (_, _) => Task.FromResult("host");
        CreateWorker(router, resolver);

        var r1 = MakeRequest("ia", "r1");
        var r2 = MakeRequest("ib", "r2");
        var r3 = MakeRequest("ia", "r3");
        await EnqueueAsync(r1, r2, r3);

        await Task.WhenAll(r1.Tcs.Task, r2.Tcs.Task, r3.Tcs.Task).WaitAsync(TimeSpan.FromSeconds(10));

        var host = (FakeDockerController)router.GetController("host");
        await Eventually.UntilAsync(() => host.StartedModels.Count == 1);
        Assert.Single(host.StartedModels);
        Assert.Empty(host.StoppedContainerIds);

        // Both non-resident models were served via the instant-switch path.
        var instantSwitches = _logStore.Entries.Count(e => e.Message.Contains("Instant switch"));
        Assert.True(instantSwitches >= 2,
            $"Expected ≥2 instant switches, got {instantSwitches}: {string.Join(" | ", _logStore.Entries.Select(e => e.Message))}");

        await ShutdownAsync();
    }

    [Fact]
    public async Task StopIncompatible_TrackedContainerWhoseRuntimeWasDeleted_IsStopped()
    {
        await RegisterRuntimeAsync("reg-doomed", "doomed-image", ["model-doomed"]);
        await RegisterRuntimeAsync("reg-survivor", "survivor-image", ["model-survivor"]);

        var router = new FakeDockerControllerRouter(new Dictionary<string, IDockerController>
        {
            ["host"] = new FakeDockerController { IdPrefix = "host" }
        });
        var resolver = new FakeModelTargetResolver();
        resolver.ResolveFunc = (_, _) => Task.FromResult("host");
        CreateWorker(router, resolver);

        // Establish residency + RunningContainers entry for the doomed runtime.
        var r1 = MakeRequest("model-doomed", "r1");
        await EnqueueAsync(r1);
        await r1.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var host = (FakeDockerController)router.GetController("host");
        await Eventually.UntilAsync(() => host.StartedModels.Count == 1);
        var doomedContainerId = host.StartedContainerIds[0];

        // Delete the registered runtime: its still-tracked running container no longer
        // maps to any registered runtime → reconcile/stop path must stop it.
        await _containerRegistry.DeleteAsync("reg-doomed");

        var r2 = MakeRequest("model-survivor", "r2");
        await EnqueueAsync(r2);
        var result2 = await r2.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(200, result2.StatusCode);

        await Eventually.UntilAsync(() => host.StoppedContainerIds.Count == 1);
        Assert.Equal(doomedContainerId, host.StoppedContainerIds[0]);
        Assert.Equal(["doomed-image", "survivor-image"], host.StartedModels);

        await ShutdownAsync();
    }

    [Fact]
    public async Task Reconcile_UnresolvableRunningContainer_NotTrackedAndNeverStopped()
    {
        await RegisterRuntimeAsync("reg-x", "image-x", ["model-x"]);
        await RegisterRuntimeAsync("reg-y", "image-y", ["model-y"]);

        var host = new FakeDockerController { IdPrefix = "host" };
        // An externally-started container with NO registry label and NO
        // RuntimeContainerId match — unresolvable, must never be tracked/stopped.
        host.ListedContainers =
        [
            new ContainerInfo
            {
                Id = "orphan-1",
                ModelId = "ghost",
                ModelName = "ghost",
                Status = ContainerStatus.Running,
                Port = 1234
            }
        ];

        var router = new FakeDockerControllerRouter(new Dictionary<string, IDockerController>
        {
            ["host"] = host
        });
        var resolver = new FakeModelTargetResolver();
        resolver.ResolveFunc = (_, _) => Task.FromResult("host");
        CreateWorker(router, resolver);

        var r1 = MakeRequest("model-x", "r1");
        var r2 = MakeRequest("model-y", "r2");
        await EnqueueAsync(r1, r2);

        await Task.WhenAll(r1.Tcs.Task, r2.Tcs.Task).WaitAsync(TimeSpan.FromSeconds(10));

        // Both incompatible runtimes switched: exactly one tracked container stopped;
        // the unresolvable orphan was left alone.
        await Eventually.UntilAsync(() => host.StartedModels.Count == 2 && host.StoppedContainerIds.Count == 1);
        Assert.DoesNotContain("orphan-1", host.StoppedContainerIds);
        Assert.All(host.StoppedContainerIds, id => Assert.StartsWith("host-", id));

        await ShutdownAsync();
    }

    public void Dispose()
    {
    }
}
