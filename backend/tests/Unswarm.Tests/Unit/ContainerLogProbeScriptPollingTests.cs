using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Unswarm.Api.BackgroundServices;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using Unswarm.Tests.Fakes;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Drives the real <see cref="ContainerLogProbe"/> background service for the
/// host-script polling path: a running host script's log file is tailed and new
/// lines are enqueued into the log store; a stale/dead script runtime is skipped.
/// Uses a real (short-lived) shell process in a temp dir — no Docker/network.
/// </summary>
public sealed class ContainerLogProbeScriptPollingTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"unswarm-logprobe-{Guid.NewGuid():N}");

    public ContainerLogProbeScriptPollingTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static ILogger<T> Log<T>() => new LoggerFactory().CreateLogger<T>();

    private string CreateScript(string body)
    {
        var path = Path.Combine(_tempDir, $"launcher-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, $"#!/bin/bash\n{body}");
        File.SetUnixFileMode(path,
            File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
        return path;
    }

    private static async Task RunProbeUntilAsync(ServiceProvider provider, FakeLogStore logStore, Func<bool> condition)
    {
        var probe = new ContainerLogProbe(provider, Log<ContainerLogProbe>());
        using var cts = new CancellationTokenSource();
        await probe.StartAsync(cts.Token);
        try
        {
            await Eventually.UntilAsync(condition, TimeSpan.FromSeconds(15));
        }
        finally
        {
            cts.Cancel();
            await probe.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task RunningHostScript_NewLogLines_AreEnqueued()
    {
        var scriptController = new HostScriptRuntimeController(Log<HostScriptRuntimeController>(), _tempDir);
        var launcher = CreateScript("echo hello-from-script; sleep 60");
        var start = await scriptController.StartScriptAsync("reg-s1", launcher, 9400);
        Assert.NotNull(start.Pid);

        try
        {
            var registry = new FakeContainerRegistry();
            await registry.CreateAsync(new RegisteredRuntime
            {
                Id = "reg-s1",
                DisplayName = "Scripty",
                Image = "scripty",
                Agent = "host",
                RuntimeKind = RuntimeKind.Script,
                RuntimeProcessId = start.Pid,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            var logStore = new FakeLogStore();
            var router = new FakeDockerControllerRouter(
                new Dictionary<string, IDockerController> { ["host"] = new FakeDockerController() });

            var services = new ServiceCollection();
            services.AddSingleton<IContainerRegistry>(registry);
            services.AddSingleton<ILogStore>(logStore);
            services.AddSingleton<IDockerControllerRouter>(router);
            services.AddSingleton(scriptController);
            await using var provider = services.BuildServiceProvider();

            await RunProbeUntilAsync(provider, logStore,
                () => logStore.Entries.Any(e => e.Source == "Scripty" && e.Message.Contains("hello-from-script")));

            Assert.Contains(logStore.Entries,
                e => e.Source == "Scripty" && e.Level == LogLevel.Info && e.Message.Contains("hello-from-script"));
        }
        finally
        {
            await scriptController.StopScriptAsync("reg-s1");
        }
    }

    [Fact]
    public async Task DeadScriptRuntime_IsSkipped_NoLinesEnqueued()
    {
        // Registered as a script runtime, but the controller never started/tracks it.
        var scriptController = new HostScriptRuntimeController(Log<HostScriptRuntimeController>(), _tempDir);
        var registry = new FakeContainerRegistry();
        await registry.CreateAsync(new RegisteredRuntime
        {
            Id = "reg-dead",
            DisplayName = "DeadScript",
            Image = "dead",
            Agent = "host",
            RuntimeKind = RuntimeKind.Script,
            RuntimeProcessId = 999999,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var logStore = new FakeLogStore();
        var router = new FakeDockerControllerRouter(
            new Dictionary<string, IDockerController> { ["host"] = new FakeDockerController() });

        var services = new ServiceCollection();
        services.AddSingleton<IContainerRegistry>(registry);
        services.AddSingleton<ILogStore>(logStore);
        services.AddSingleton<IDockerControllerRouter>(router);
        services.AddSingleton(scriptController);
        await using var provider = services.BuildServiceProvider();

        var probe = new ContainerLogProbe(provider, Log<ContainerLogProbe>());
        using var cts = new CancellationTokenSource();
        await probe.StartAsync(cts.Token);
        try
        {
            // First poll is immediate; give it a comfortable margin, then assert absence.
            await Task.Delay(1000);
        }
        finally
        {
            cts.Cancel();
            await probe.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.DoesNotContain(logStore.Entries, e => e.Source == "DeadScript");
    }
}
