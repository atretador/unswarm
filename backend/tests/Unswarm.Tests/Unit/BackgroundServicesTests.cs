using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;
using Unswarm.Core.Services;
using Unswarm.Api.BackgroundServices;
using Unswarm.Tests.Fakes;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Real (not simulated) background service coverage: IdleShutdownService,
/// HealthCheckService, ContainerLogProbe and LogRetentionService are driven through
/// IHostedService.StartAsync/StopAsync against fake dependencies.
/// </summary>
public sealed class BackgroundServicesTests
{
    private static ILogger<T> Log<T>() => new LoggerFactory().CreateLogger<T>();

    private ServiceProvider BuildProvider(Action<ServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDockerController>(new FakeDockerController());
        services.AddSingleton<ILogStore>(new FakeLogStore());
        services.AddSingleton<ISettingsStore>(new FakeSettingsStore());
        services.AddSingleton<IClock>(new FakeClock());
        services.AddSingleton<IContainerRegistry>(new FakeContainerRegistry());
        services.AddSingleton<IHealthChecker>(new FakeHealthChecker());
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static async Task RunServiceAsync(
        BackgroundService service,
        Func<Task> act,
        TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            await act();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(timeout ?? TimeSpan.FromSeconds(10));
            cts.Cancel();
        }
    }

    // ── IdleShutdownService ───────────────────────────────────────────────────

    [Fact]
    public async Task IdleShutdown_StopsOnlyIdleManagedContainers()
    {
        var registry = new FakeContainerRegistry();
        await registry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-idle", DisplayName = "Idle Model", Image = "idle:latest",
            RuntimeContainerId = "registered-idle",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await registry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-busy", DisplayName = "Busy Model", Image = "busy:latest",
            RuntimeContainerId = "registered-busy",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });

        var docker = new FakeDockerController();
        docker.ListedContainers =
        [
            new ContainerInfo { Id = "registered-idle", ModelId = "idle-model", ModelName = "Idle Model", Status = ContainerStatus.Running, Uptime = 999 },
            new ContainerInfo { Id = "registered-busy", ModelId = "busy-model", ModelName = "Busy Model", Status = ContainerStatus.Running, Uptime = 3 },
            new ContainerInfo { Id = "orphan-1", ModelId = "orphan-m", ModelName = "Orphan", Status = ContainerStatus.Running, Uptime = 999 }
        ];

        var logStore = new FakeLogStore();
        var provider = BuildProvider(s =>
        {
            s.AddSingleton<IDockerController>(docker);
            s.AddSingleton<ILogStore>(logStore);
            s.AddSingleton<IContainerRegistry>(registry);
            s.AddSingleton<ISettingsStore>(new FakeSettingsStore(new Settings
            {
                AutoShutdownIdle = true,
                IdleTimeout = 10
            }));
        });

        await RunServiceAsync(new IdleShutdownService(provider, Log<IdleShutdownService>()), async () =>
        {
            await Eventually.UntilAsync(() => docker.StoppedContainerIds.Count == 1);
            Assert.Equal(["registered-idle"], docker.StoppedContainerIds);
            Assert.Contains("Shutting down idle container", string.Join("|", logStore.Entries.Select(e => e.Message)));
        });
    }

    [Fact]
    public async Task IdleShutdown_StopFailure_LogsErrorAndContinues()
    {
        var registry = new FakeContainerRegistry();
        await registry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-a", DisplayName = "A", Image = "a:latest",
            RuntimeContainerId = "container-idle-a",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await registry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-b", DisplayName = "B", Image = "b:latest",
            RuntimeContainerId = "container-idle-b",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });

        var docker = new FakeDockerController();
        docker.OnStop = (id, ct) => id == "container-idle-a"
            ? throw new InvalidOperationException("docker daemon gone")
            : Task.CompletedTask;
        docker.ListedContainers =
        [
            new ContainerInfo { Id = "container-idle-a", ModelId = "a-m", ModelName = "A", Status = ContainerStatus.Running, Uptime = 500 },
            new ContainerInfo { Id = "container-idle-b", ModelId = "b-m", ModelName = "B", Status = ContainerStatus.Running, Uptime = 500 }
        ];

        var logStore = new FakeLogStore();
        var provider = BuildProvider(s =>
        {
            s.AddSingleton<IDockerController>(docker);
            s.AddSingleton<ILogStore>(logStore);
            s.AddSingleton<IContainerRegistry>(registry);
            s.AddSingleton<ISettingsStore>(new FakeSettingsStore(new Settings
            {
                AutoShutdownIdle = true,
                IdleTimeout = 10
            }));
        });

        await RunServiceAsync(new IdleShutdownService(provider, Log<IdleShutdownService>()), async () =>
        {
            // The failed stop is logged but does not prevent stopping the next container.
            await Eventually.UntilAsync(() => docker.StoppedContainerIds.Count == 2);
            Assert.Equal(["container-idle-a", "container-idle-b"], docker.StoppedContainerIds);
            Assert.Contains(logStore.Entries,
                e => e.Level == LogLevel.Error && e.Message.Contains("Failed to stop idle container"));
        });
    }

    [Fact]
    public async Task IdleShutdown_Disabled_DoesNotStopAnything()
    {
        var docker = new FakeDockerController();
        docker.ListedContainers =
        [
            new ContainerInfo { Id = "container-idle-1", ModelId = "m-m", ModelName = "M", Status = ContainerStatus.Running, Uptime = 9999 }
        ];
        var registry = new FakeContainerRegistry();
        await registry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-1", DisplayName = "M", Image = "m:latest",
            RuntimeContainerId = "container-idle-1",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });

        var provider = BuildProvider(s =>
        {
            s.AddSingleton<IDockerController>(docker);
            s.AddSingleton<IContainerRegistry>(registry);
            s.AddSingleton<ISettingsStore>(new FakeSettingsStore(new Settings
            {
                AutoShutdownIdle = false
            }));
        });

        await RunServiceAsync(new IdleShutdownService(provider, Log<IdleShutdownService>()), async () =>
        {
            // Grace period for any (incorrect) immediate stop attempt.
            await Task.Delay(400);
            Assert.Empty(docker.StoppedContainerIds);
        });
    }

    // ── HealthCheckService ────────────────────────────────────────────────────

    [Fact]
    public async Task HealthCheck_UnhealthyManagedContainer_LogsWarning()
    {
        var registry = new FakeContainerRegistry();
        await registry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-h", DisplayName = "H", Image = "h:latest",
            RuntimeContainerId = "container-health",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });

        var docker = new FakeDockerController();
        docker.ListedContainers =
        [
            new ContainerInfo { Id = "container-health", ModelId = "h-m", ModelName = "H", Status = ContainerStatus.Running, Port = 8081 },
            new ContainerInfo { Id = "container-unmanaged", ModelId = "u-m", ModelName = "U", Status = ContainerStatus.Running, Port = 8082 }
        ];
        var healthChecker = new FakeHealthChecker { CheckFunc = (_, _) => Task.FromResult(false) };
        var logStore = new FakeLogStore();

        var provider = BuildProvider(s =>
        {
            s.AddSingleton<IDockerController>(docker);
            s.AddSingleton<IHealthChecker>(healthChecker);
            s.AddSingleton<ILogStore>(logStore);
            s.AddSingleton<IContainerRegistry>(registry);
            s.AddSingleton<ISettingsStore>(new FakeSettingsStore(new Settings { HealthCheckInterval = 5 }));
        });

        await RunServiceAsync(new HealthCheckService(provider, Log<HealthCheckService>()), async () =>
        {
            await Eventually.UntilAsync(() => healthChecker.CheckedPorts.Count >= 1);
            Assert.Contains("is unhealthy", string.Join("|", logStore.Entries.Select(e => e.Message)));

            // Only the managed container was probed.
            Assert.Single(healthChecker.CheckedPorts);
            Assert.Equal(8081, healthChecker.CheckedPorts[0]);
        });
    }

    [Fact]
    public async Task HealthCheck_CheckerThrows_LogsErrorAndKeepsRunning()
    {
        var registry = new FakeContainerRegistry();
        await registry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-t", DisplayName = "T", Image = "t:latest",
            RuntimeContainerId = "container-throwing",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });

        var docker = new FakeDockerController();
        docker.ListedContainers =
        [
            new ContainerInfo { Id = "container-throwing", ModelId = "t-m", ModelName = "T", Status = ContainerStatus.Running, Port = 9099 }
        ];
        var healthChecker = new FakeHealthChecker
        {
            CheckFunc = (_, _) => throw new InvalidOperationException("probe exploded")
        };
        var logStore = new FakeLogStore();

        var provider = BuildProvider(s =>
        {
            s.AddSingleton<IDockerController>(docker);
            s.AddSingleton<IHealthChecker>(healthChecker);
            s.AddSingleton<ILogStore>(logStore);
            s.AddSingleton<IContainerRegistry>(registry);
            s.AddSingleton<ISettingsStore>(new FakeSettingsStore(new Settings { HealthCheckInterval = 5 }));
        });

        await RunServiceAsync(new HealthCheckService(provider, Log<HealthCheckService>()), async () =>
        {
            await Eventually.UntilAsync(() => logStore.Entries.Any(e =>
                e.Level == LogLevel.Error && e.Message.Contains("Health check failed")));
            Assert.Contains("probe exploded", logStore.Entries.First(e => e.Level == LogLevel.Error).Message);
        });
    }

    // ── ContainerLogProbe ─────────────────────────────────────────────────────

    private static (ServiceProvider Provider, FakeLogStore LogStore, FakeDockerController Docker) BuildProbeProvider(
        FakeContainerRegistry registry,
        HostScriptRuntimeController? scriptController = null)
    {
        var docker = new FakeDockerController();
        var logStore = new FakeLogStore();
        var router = new FakeDockerControllerRouter(new Dictionary<string, IDockerController>
        {
            ["host"] = docker
        });
        var services = new ServiceCollection();
        services.AddSingleton<IContainerRegistry>(registry);
        services.AddSingleton<ILogStore>(logStore);
        services.AddSingleton<IDockerControllerRouter>(router);
        services.AddSingleton(scriptController ?? new HostScriptRuntimeController(
            Log<HostScriptRuntimeController>(),
            Path.Combine(Path.GetTempPath(), "unswarm-probe-" + Guid.NewGuid().ToString("N"))));

        return (services.BuildServiceProvider(), logStore, docker);
    }

    [Fact]
    public async Task LogProbe_FirstPollEnqueuesClassifiedContainerLines()
    {
        var registry = new FakeContainerRegistry();
        await registry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-log", DisplayName = "Loggy", Image = "loggy:latest",
            Agent = "host",
            RuntimeKind = RuntimeKind.Container,
            RuntimeContainerId = "cid-log",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        // An agent runtime whose target is unreachable must be skipped quietly.
        await registry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-offline", DisplayName = "Offline", Image = "off:latest",
            Agent = "gpu-x",
            RuntimeKind = RuntimeKind.Container,
            RuntimeContainerId = "cid-off",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });

        var (provider, logStore, docker) = BuildProbeProvider(registry);
        docker.OnGetContainerLogs = (id, tail, ct) => id == "cid-log"
            ? Task.FromResult<IReadOnlyList<string>>([
                "server started",
                "WARNING slow disk detected",
                "FATAL exception in worker"
            ])
            : Task.FromResult<IReadOnlyList<string>>([]);

        await RunServiceAsync(new ContainerLogProbe(provider, Log<ContainerLogProbe>()), async () =>
        {
            await Eventually.UntilAsync(() => logStore.Entries.Count >= 3);

            Assert.Contains(logStore.Entries, e => e.Source == "Loggy" && e.Level == LogLevel.Info && e.Message == "server started");
            Assert.Contains(logStore.Entries, e => e.Source == "Loggy" && e.Level == LogLevel.Warn && e.Message.Contains("WARNING slow"));
            Assert.Contains(logStore.Entries, e => e.Source == "Loggy" && e.Level == LogLevel.Error && e.Message.Contains("FATAL exception"));
        });
    }

    [Fact]
    public async Task LogProbe_SecondPollEnqueuesOnlyNewLines()
    {
        var registry = new FakeContainerRegistry();
        await registry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-dedup", DisplayName = "Dedup", Image = "dedup:latest",
            Agent = "host",
            RuntimeKind = RuntimeKind.Container,
            RuntimeContainerId = "cid-dedup",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });

        var (provider, logStore, docker) = BuildProbeProvider(registry);
        var lines = new List<string> { "line-one", "line-two" };
        docker.OnGetContainerLogs = (_, _, _) => Task.FromResult<IReadOnlyList<string>>(lines.ToList());

        await RunServiceAsync(new ContainerLogProbe(provider, Log<ContainerLogProbe>()), async () =>
        {
            // First poll enqueues the initial history.
            await Eventually.UntilAsync(() => logStore.Entries.Count(e => e.Source == "Dedup") == 2);

            // Append a new line; the next poll (5s interval) must enqueue ONLY it.
            lines.Add("line-three");
            await Eventually.UntilAsync(
                () => logStore.Entries.Any(e => e.Message == "line-three"),
                timeout: TimeSpan.FromSeconds(15));

            // Dedup: earlier lines were never re-enqueued.
            Assert.Equal(1, logStore.Entries.Count(e => e.Message == "line-one"));
            Assert.Equal(1, logStore.Entries.Count(e => e.Message == "line-two"));
            Assert.Equal(1, logStore.Entries.Count(e => e.Message == "line-three"));
        }, timeout: TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task LogProbe_ScriptLogs_EnqueuedWithStderrClassification()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "unswarm-probe-script-" + Guid.NewGuid().ToString("N"));
        var scriptController = new HostScriptRuntimeController(Log<HostScriptRuntimeController>(), tempDir);
        try
        {
            var launcher = Path.Combine(tempDir, "launcher.sh");
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(launcher, "#!/bin/bash\nsleep 30\n");

            var startResult = await scriptController.StartScriptAsync("reg-scr", launcher, 9000);
            Assert.Null(startResult.ErrorMessage);

            var registry = new FakeContainerRegistry();
            await registry.CreateAsync(new RegisteredRuntime
            {
                Id = "reg-scr", DisplayName = "Scrippy", Image = "scrippy",
                Agent = "host",
                RuntimeKind = RuntimeKind.Script,
                RuntimeProcessId = startResult.Pid,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });

            // Simulate script output by appending to the controller's log file.
            var logFile = Path.Combine(tempDir, "script-logs", "reg-scr.log");
            await File.AppendAllTextAsync(logFile, "[stdout] hello from script\n[stderr] something went sideways\n");

            var (provider, logStore, _) = BuildProbeProvider(registry, scriptController);

            await RunServiceAsync(new ContainerLogProbe(provider, Log<ContainerLogProbe>()), async () =>
            {
                await Eventually.UntilAsync(() => logStore.Entries.Count(e => e.Source == "Scrippy") >= 2);
                Assert.Contains(logStore.Entries, e => e.Source == "Scrippy" && e.Level == LogLevel.Info && e.Message.Contains("[stdout] hello"));
                Assert.Contains(logStore.Entries, e => e.Source == "Scrippy" && e.Level == LogLevel.Warn && e.Message.Contains("[stderr] something"));
            });
        }
        finally
        {
            await scriptController.StopScriptAsync("reg-scr");
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // ── LogRetentionService ───────────────────────────────────────────────────

    [Fact]
    public async Task LogRetention_PrunesEntriesOlderThanCutoff()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbFactory = () =>
        {
            var options = new DbContextOptionsBuilder<UnswarmDbContext>()
                .UseSqlite(connection)
                .Options;
            return new UnswarmDbContext(options);
        };
        using (var db = dbFactory())
        {
            db.Database.EnsureCreated();
            var now = DateTimeOffset.UtcNow;
            db.Logs.AddRange(
                new LogEntity { Id = "old-1", Timestamp = now.AddHours(-5), TimestampTicks = now.AddHours(-5).UtcTicks, Level = "Info", Source = "s", Message = "old one" },
                new LogEntity { Id = "old-2", Timestamp = now.AddHours(-3), TimestampTicks = now.AddHours(-3).UtcTicks, Level = "Warn", Source = "s", Message = "also old" },
                new LogEntity { Id = "fresh-1", Timestamp = now, TimestampTicks = now.UtcTicks, Level = "Info", Source = "s", Message = "fresh" });
            db.SaveChanges();
        }

        var services = new ServiceCollection();
        services.AddSingleton<ISettingsStore>(new FakeSettingsStore(new Settings { LogRetention = 1 })); // 1 hour
        services.AddSingleton<Func<UnswarmDbContext>>(dbFactory);
        var provider = services.BuildServiceProvider();

        await RunServiceAsync(new LogRetentionService(provider, Log<LogRetentionService>()), async () =>
        {
            await Eventually.UntilAsync(() =>
            {
                using var db = dbFactory();
                return db.Logs.Count() == 1;
            });

            using var verify = dbFactory();
            var remaining = verify.Logs.Select(l => l.Id).ToList();
            Assert.Equal(["fresh-1"], remaining);
        });

        await connection.DisposeAsync();
    }

    /// <summary>Guard against accidental JSON regressions in retention settings wiring.</summary>
    [Fact]
    public void SettingsDefaults_RetentionSane()
    {
        var settings = new Settings();
        Assert.True(settings.LogRetention >= 1);
        Assert.True(settings.HealthCheckInterval >= 1);
        JsonSerializer.Serialize(settings);
    }
}
