using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using Unswarm.Api.BackgroundServices;
using Unswarm.Tests.Fakes;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests for IdleShutdownService driving the REAL service against fakes, pinning
/// the activity-anchored idle semantics: the idle timer is anchored to the
/// scheduler's last recorded activity per runtime (not container-creation uptime),
/// runtimes with pending/in-flight work are never stopped, stops go through the
/// scheduler-aware ISchedulerDrainer path (clearing lane residency), script
/// runtimes get the same guard, and non-scheduler-managed units fall back to the
/// legacy direct stop.
/// </summary>
public sealed class IdleShutdownServiceTests : IDisposable
{
    private readonly FakeClock _clock = new(); // 2025-01-01T00:00:00Z
    private readonly FakeDockerController _docker = new();
    private readonly FakeLogStore _logStore = new();
    private readonly FakeContainerRegistry _registry = new();
    private readonly FakeSchedulerDrainer _drainer = new();

    private ServiceProvider BuildProvider(Settings? settings = null, HostScriptRuntimeController? scriptController = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDockerController>(_docker);
        services.AddSingleton<ILogStore>(_logStore);
        services.AddSingleton<IClock>(_clock);
        services.AddSingleton<IContainerRegistry>(_registry);
        services.AddSingleton<ISettingsStore>(new FakeSettingsStore(settings ?? new Settings
        {
            AutoShutdownIdle = true,
            IdleTimeout = 10 // seconds
        }));
        services.AddSingleton<ISchedulerDrainer>(_drainer);
        if (scriptController is not null)
            services.AddSingleton(scriptController);
        return services.BuildServiceProvider();
    }

    private static async Task RunOneTickAsync(IdleShutdownService service, Func<Task> act)
    {
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            await act();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            cts.Cancel();
        }
    }

    private RegisteredRuntime NewRuntime(string id, string name, string? containerId = null) => new()
    {
        Id = id,
        DisplayName = name,
        Image = $"{id}:latest",
        RuntimeContainerId = containerId,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private ContainerInfo RunningContainer(string id, string runtimeId, int uptime) => new()
    {
        Id = id,
        ModelId = "m-" + runtimeId,
        ModelName = "Model " + runtimeId,
        Status = ContainerStatus.Running,
        Uptime = uptime,
        RegisteredRuntimeId = runtimeId
    };

    [Fact]
    public async Task RecentActivity_NotStopped_EvenWhenTotalUptimeExceedsTimeout()
    {
        await _registry.CreateAsync(NewRuntime("reg-recent", "Recent", "cid-recent"));
        _docker.ListedContainers = [RunningContainer("cid-recent", "reg-recent", uptime: 999)];
        // Served a request 5s ago — inside the 10s idle window.
        _drainer.SetLastActivityUtc("reg-recent", _clock.UtcNow.UtcDateTime - TimeSpan.FromSeconds(5));

        await using var provider = BuildProvider();
        await RunOneTickAsync(new IdleShutdownService(provider, Log<IdleShutdownService>()), async () =>
        {
            await Task.Delay(400); // grace for any (incorrect) stop attempt
            Assert.Empty(_docker.StoppedContainerIds);
            Assert.Empty(_drainer.StopCalls);
        });
    }

    [Fact]
    public async Task PendingWork_NotStopped_EvenWhenActivityAnchorIsOld()
    {
        await _registry.CreateAsync(NewRuntime("reg-queued", "Queued", "cid-queued"));
        _docker.ListedContainers = [RunningContainer("cid-queued", "reg-queued", uptime: 999)];
        // Activity anchor is far past the threshold...
        _drainer.SetLastActivityUtc("reg-queued", _clock.UtcNow.UtcDateTime - TimeSpan.FromSeconds(999));
        // ...but queued/hot-conversation work exists → never stop-eligible.
        _drainer.SetPendingWork("reg-queued", true);

        await using var provider = BuildProvider();
        await RunOneTickAsync(new IdleShutdownService(provider, Log<IdleShutdownService>()), async () =>
        {
            await Task.Delay(400);
            Assert.Empty(_docker.StoppedContainerIds);
            Assert.Empty(_drainer.StopCalls);
        });
    }

    [Fact]
    public async Task BusyRaceDuringStop_KeepsContainerRunning()
    {
        await _registry.CreateAsync(NewRuntime("reg-race", "Race", "cid-race"));
        _docker.ListedContainers = [RunningContainer("cid-race", "reg-race", uptime: 999)];
        _drainer.SetLastActivityUtc("reg-race", _clock.UtcNow.UtcDateTime - TimeSpan.FromSeconds(999));
        // Work raced in between the service's guard and the scheduler stop.
        _drainer.OnStopIdle = (_, _) => IdleStopResult.Busy;

        await using var provider = BuildProvider();
        await RunOneTickAsync(new IdleShutdownService(provider, Log<IdleShutdownService>()), async () =>
        {
            await Eventually.UntilAsync(() => _drainer.StopCalls.Count == 1);
            await Task.Delay(200); // grace for any (incorrect) direct-stop fallback
            Assert.Empty(_docker.StoppedContainerIds);
        });
    }

    [Fact]
    public async Task IdleBeyondTimeout_StoppedThroughSchedulerPath_DirectStopSkipped()
    {
        await _registry.CreateAsync(NewRuntime("reg-idle", "Idle", "cid-idle"));
        _docker.ListedContainers = [RunningContainer("cid-idle", "reg-idle", uptime: 999)];
        _drainer.SetLastActivityUtc("reg-idle", _clock.UtcNow.UtcDateTime - TimeSpan.FromSeconds(999));

        await using var provider = BuildProvider();
        await RunOneTickAsync(new IdleShutdownService(provider, Log<IdleShutdownService>()), async () =>
        {
            await Eventually.UntilAsync(() => _drainer.StopCalls.Count == 1);
            // Stopped THROUGH the scheduler (residency cleared there) — the raw
            // docker stop must NOT fire on top of it.
            Assert.Equal([("reg-idle", "cid-idle")], _drainer.StopCalls);
            Assert.DoesNotContain("cid-idle", _docker.StoppedContainerIds);
            Assert.Contains(_logStore.Entries,
                e => e.Level == LogLevel.Info && e.Message.Contains("Shutting down idle container"));
        });
    }

    [Fact]
    public async Task ScriptRuntime_GuardedSameWay_RecentActivityNotStopped()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "unswarm-idle-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var launcher = Path.Combine(tempDir, "launcher.sh");
        File.WriteAllText(launcher, "#!/bin/bash\nsleep 60\n");
        var scriptController = new HostScriptRuntimeController(Log<HostScriptRuntimeController>(), tempDir);

        try
        {
            var startResult = await scriptController.StartScriptAsync("reg-scr", launcher, 9300);
            Assert.Null(startResult.ErrorMessage);

            await _registry.CreateAsync(new RegisteredRuntime
            {
                Id = "reg-scr",
                DisplayName = "Scrippy",
                Image = "scrippy",
                RuntimeKind = RuntimeKind.Script,
                RuntimeProcessId = startResult.Pid,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            // Script process has been up "forever" relative to the threshold, but
            // it served a request 2s ago — must NOT be stopped.
            _drainer.SetLastActivityUtc("reg-scr", _clock.UtcNow.UtcDateTime - TimeSpan.FromSeconds(2));

            await using var provider = BuildProvider(scriptController: scriptController);
            await RunOneTickAsync(new IdleShutdownService(provider, Log<IdleShutdownService>()), async () =>
            {
                await Task.Delay(400);
                Assert.Empty(_drainer.StopCalls);
                Assert.True(scriptController.GetUptime("reg-scr").HasValue, "script process should still be running");
            });
        }
        finally
        {
            await scriptController.StopScriptAsync("reg-scr");
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ScriptRuntime_IdleBeyondTimeout_StoppedThroughSchedulerPathWithLaneKey()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "unswarm-idle-script2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var launcher = Path.Combine(tempDir, "launcher.sh");
        File.WriteAllText(launcher, "#!/bin/bash\nsleep 60\n");
        var scriptController = new HostScriptRuntimeController(Log<HostScriptRuntimeController>(), tempDir);

        try
        {
            var startResult = await scriptController.StartScriptAsync("reg-scr2", launcher, 9301);
            Assert.Null(startResult.ErrorMessage);

            await _registry.CreateAsync(new RegisteredRuntime
            {
                Id = "reg-scr2",
                DisplayName = "Scrippy II",
                Image = "scrippy2",
                RuntimeKind = RuntimeKind.Script,
                RuntimeProcessId = startResult.Pid,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            _drainer.SetLastActivityUtc("reg-scr2", _clock.UtcNow.UtcDateTime - TimeSpan.FromSeconds(999));

            await using var provider = BuildProvider(scriptController: scriptController);
            await RunOneTickAsync(new IdleShutdownService(provider, Log<IdleShutdownService>()), async () =>
            {
                await Eventually.UntilAsync(() => _drainer.StopCalls.Count == 1);
                // Scripts use the "script:<regId>" lane-residency key.
                Assert.Equal([("reg-scr2", "script:reg-scr2")], _drainer.StopCalls);
                Assert.Empty(_docker.StoppedContainerIds);
            });
        }
        finally
        {
            await scriptController.StopScriptAsync("reg-scr2");
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task NotSchedulerManaged_FallsBackToDirectStop()
    {
        await _registry.CreateAsync(NewRuntime("reg-orphan", "Orphaned", "cid-orphan"));
        _docker.ListedContainers = [RunningContainer("cid-orphan", "reg-orphan", uptime: 999)];
        _drainer.SetLastActivityUtc("reg-orphan", _clock.UtcNow.UtcDateTime - TimeSpan.FromSeconds(999));
        // Scheduler reports the runtime as not managed (no lanes).
        _drainer.OnStopIdle = (_, _) => IdleStopResult.NotManaged;

        await using var provider = BuildProvider();
        await RunOneTickAsync(new IdleShutdownService(provider, Log<IdleShutdownService>()), async () =>
        {
            await Eventually.UntilAsync(() => _docker.StoppedContainerIds.Count == 1);
            Assert.Equal(["cid-orphan"], _docker.StoppedContainerIds);
        });
    }

    [Fact]
    public async Task NoDrainerRegistered_LegacyUptimeSemanticsPreserved()
    {
        // Units with no scheduler coverage at all keep the old behavior:
        // total uptime past the threshold → direct stop.
        await _registry.CreateAsync(NewRuntime("reg-legacy", "Legacy", "cid-legacy"));
        _docker.ListedContainers = [RunningContainer("cid-legacy", "reg-legacy", uptime: 999)];

        var services = new ServiceCollection();
        services.AddSingleton<IDockerController>(_docker);
        services.AddSingleton<ILogStore>(_logStore);
        services.AddSingleton<IClock>(_clock);
        services.AddSingleton<IContainerRegistry>(_registry);
        services.AddSingleton<ISettingsStore>(new FakeSettingsStore(new Settings
        {
            AutoShutdownIdle = true,
            IdleTimeout = 10
        }));
        // No ISchedulerDrainer registered — guard silently no-ops (legacy deployments).
        await using var provider = services.BuildServiceProvider();

        await RunOneTickAsync(new IdleShutdownService(provider, Log<IdleShutdownService>()), async () =>
        {
            await Eventually.UntilAsync(() => _docker.StoppedContainerIds.Count == 1);
            Assert.Equal(["cid-legacy"], _docker.StoppedContainerIds);
        });
    }

    private static ILogger<T> Log<T>() => new LoggerFactory().CreateLogger<T>();

    public void Dispose()
    {
    }
}
