using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Api.BackgroundServices;

/// <summary>
/// If AutoShutdownIdle is enabled, stops containers that have been idle for IdleTimeout seconds.
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

                var containers = await docker.ListContainersAsync(stoppingToken);
                var idleThreshold = TimeSpan.FromSeconds(settings.IdleTimeout);

                foreach (var container in containers.Where(c => c.Status == ContainerStatus.Running))
                {
                    if (container.Uptime > 0 && TimeSpan.FromSeconds(container.Uptime) > idleThreshold)
                    {
                        logStore.Enqueue(LogLevel.Info, "IdleShutdown",
                            $"Shutting down idle container {container.Id[..12]} (model: {container.ModelName}, uptime: {container.Uptime}s)");

                        try
                        {
                            await docker.StopContainerAsync(container.Id, stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            logStore.Enqueue(LogLevel.Error, "IdleShutdown",
                                $"Failed to stop idle container {container.Id[..12]}: {ex.Message}");
                        }
                    }
                }

                // Script runtimes: check idle scripts and stop them
                try
                {
                    var scriptController = scope.ServiceProvider.GetService<HostScriptRuntimeController>();
                    var registry = scope.ServiceProvider.GetService<IContainerRegistry>();

                    if (scriptController is not null && registry is not null)
                    {
                        var allRuntimes = await registry.ListAllAsync(stoppingToken);
                        foreach (var runtime in allRuntimes.Where(r => r.RuntimeKind == RuntimeKind.Script && r.RuntimeProcessId.HasValue))
                        {
                            var uptime = scriptController.GetUptime(runtime.Id);
                            if (uptime.HasValue && uptime.Value > idleThreshold)
                            {
                                logStore.Enqueue(LogLevel.Info, "IdleShutdown",
                                    $"Shutting down idle script {runtime.Id[..Math.Min(12, runtime.Id.Length)]} (model: {runtime.Image}, uptime: {uptime.Value.TotalSeconds:F0}s)");

                                try
                                {
                                    await scriptController.StopScriptAsync(runtime.Id, stoppingToken);
                                }
                                catch (Exception ex)
                                {
                                    logStore.Enqueue(LogLevel.Error, "IdleShutdown",
                                        $"Failed to stop idle script {runtime.Id[..Math.Min(12, runtime.Id.Length)]}: {ex.Message}");
                                }
                            }
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
}
