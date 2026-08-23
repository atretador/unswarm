using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Api.BackgroundServices;

/// <summary>
/// If AutoShutdownIdle is enabled, stops containers and script runtimes that have
/// been idle for IdleTimeout seconds. "Idle" is anchored to the scheduler's last
/// recorded activity for the owning registered runtime (request enqueue, start, or
/// completion) — NOT to container creation uptime — and a runtime with queued or
/// in-flight work (or a hot conversation hold) is never stopped. Stops go through
/// the scheduler-aware <see cref="ISchedulerDrainer.StopIdleRuntimeAsync"/> so lane
/// residency is cleared; units the scheduler does not manage fall back to the
/// legacy direct-stop behavior.
/// </summary>
public sealed class IdleShutdownService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<IdleShutdownService> _logger;

    public IdleShutdownService(IServiceProvider services, ILogger<IdleShutdownService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IdleShutdownService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _services.CreateAsyncScope();
                var docker = scope.ServiceProvider.GetRequiredService<IDockerController>();
                var logStore = scope.ServiceProvider.GetRequiredService<ILogStore>();
                var settingsStore = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
                var clock = scope.ServiceProvider.GetRequiredService<IClock>();

                var settings = await settingsStore.GetAsync(stoppingToken);

                if (!settings.AutoShutdownIdle)
                {
                    await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                    continue;
                }

                var idleThreshold = TimeSpan.FromSeconds(settings.IdleTimeout);
                var drainer = scope.ServiceProvider.GetService<ISchedulerDrainer>();

                var containers = await docker.ListContainersAsync(stoppingToken);

                var registry = scope.ServiceProvider.GetRequiredService<IContainerRegistry>();
                var registeredContainers = await registry.ListAllAsync(stoppingToken);
                // Registry id lookup by RuntimeContainerId — used when the docker
                // label path (ContainerInfo.RegisteredRuntimeId) is absent.
                var runtimeIdByContainerId = new Dictionary<string, string>(
                    registeredContainers
                        .Where(r => r.RuntimeContainerId is not null)
                        .Select(r => new KeyValuePair<string, string>(r.RuntimeContainerId!, r.Id)),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var container in containers.Where(c => c.Status == ContainerStatus.Running))
                {
                    // Accept membership via RuntimeContainerId OR via the docker-label path
                    // (ContainerInfo.RegisteredRuntimeId) which is set during container registration.
                    string? runtimeId = container.RegisteredRuntimeId;
                    bool isManaged = !string.IsNullOrEmpty(runtimeId)
                        || runtimeIdByContainerId.TryGetValue(container.Id, out runtimeId);

                    if (!isManaged || runtimeId is null)
                        continue;

                    await StopIfIdleAsync(
                        runtimeId,
                        container.Id,
                        isScript: false,
                        FallbackIdle: () => container.Uptime > 0 && TimeSpan.FromSeconds(container.Uptime) > idleThreshold,
                        DirectStopAsync: () => docker.StopContainerAsync(container.Id, stoppingToken),
                        logStore, clock, drainer, idleThreshold, stoppingToken);
                }

                // Script runtimes: same activity-anchored, scheduler-aware guard.
                try
                {
                    var scriptController = scope.ServiceProvider.GetService<HostScriptRuntimeController>();

                    if (scriptController is not null)
                    {
                        var allRuntimes = await registry.ListAllAsync(stoppingToken);
                        foreach (var runtime in allRuntimes.Where(r => r.RuntimeKind == RuntimeKind.Script && r.RuntimeProcessId.HasValue))
                        {
                            // Lane residency key for scripts is "script:<regId>".
                            await StopIfIdleAsync(
                                runtime.Id,
                                $"script:{runtime.Id}",
                                isScript: true,
                                FallbackIdle: () =>
                                {
                                    var uptime = scriptController.GetUptime(runtime.Id);
                                    return uptime.HasValue && uptime.Value > idleThreshold;
                                },
                                DirectStopAsync: () => scriptController.StopScriptAsync(runtime.Id, stoppingToken),
                                logStore, clock, drainer, idleThreshold, stoppingToken);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error checking idle script runtimes");
                }

                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IdleShutdownService error");
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }
        }

        _logger.LogInformation("IdleShutdownService stopped");
    }

    /// <summary>
    /// Shared stop decision for one managed unit (container or script):
    ///  1. Idle anchor — scheduler-recorded last activity when available; otherwise
    ///     the legacy total-uptime anchor (no scheduler traffic observed since
    ///     process start preserves the old semantics).
    ///  2. Pending-work guard — never stop while in-flight/queued/hot-conversation
    ///     work exists for the runtime.
    ///  3. Scheduler-aware stop via <see cref="ISchedulerDrainer.StopIdleRuntimeAsync"/>;
    ///     NotManaged (or no drainer registered at all) falls back to the legacy
    ///     direct stop via <paramref name="DirectStopAsync"/>.
    /// </summary>
    private static async Task StopIfIdleAsync(
        string runtimeId,
        string unitId,
        bool isScript,
        Func<bool> FallbackIdle,
        Func<Task> DirectStopAsync,
        ILogStore logStore,
        IClock clock,
        ISchedulerDrainer? drainer,
        TimeSpan idleThreshold,
        CancellationToken ct)
    {
        // ── 1. Activity-anchored idle check ──────────────────────────────────
        var lastActivity = drainer?.GetLastActivityUtc(runtimeId);
        bool idleBeyondThreshold = lastActivity.HasValue
            ? clock.UtcNow.UtcDateTime - lastActivity.Value > idleThreshold
            : FallbackIdle();

        if (!idleBeyondThreshold)
            return;

        // ── 2. Pending-work guard ────────────────────────────────────────────
        if (drainer is not null && drainer.HasPendingWork(runtimeId))
        {
            logStore.Enqueue(LogLevel.Debug, "IdleShutdown",
                $"Skipping {unitId[..Math.Min(12, unitId.Length)]} — runtime {runtimeId} still has pending or in-flight work");
            return;
        }

        // ── 3. Scheduler-aware stop with direct-stop fallback ────────────────
        if (drainer is not null)
        {
            var result = await drainer.StopIdleRuntimeAsync(runtimeId, unitId, ct);
            switch (result)
            {
                case IdleStopResult.Stopped:
                    logStore.Enqueue(LogLevel.Info, "IdleShutdown",
                        $"Shutting down idle {(isScript ? "script" : "container")} {unitId[..Math.Min(12, unitId.Length)]} (runtime: {runtimeId})");
                    return;
                case IdleStopResult.Busy:
                    logStore.Enqueue(LogLevel.Debug, "IdleShutdown",
                        $"Skipping {unitId[..Math.Min(12, unitId.Length)]} — became busy during the idle-stop race");
                    return;
                case IdleStopResult.NotManaged:
                    break; // fall through to the legacy direct stop below
            }
        }
        else
        {
            logStore.Enqueue(LogLevel.Info, "IdleShutdown",
                $"Shutting down idle {(isScript ? "script" : "container")} {unitId[..Math.Min(12, unitId.Length)]}");
        }

        try
        {
            await DirectStopAsync();
        }
        catch (Exception ex)
        {
            logStore.Enqueue(LogLevel.Error, "IdleShutdown",
                $"Failed to stop idle {(isScript ? "script" : "container")} {unitId[..Math.Min(12, unitId.Length)]}: {ex.Message}");
        }
    }
}
