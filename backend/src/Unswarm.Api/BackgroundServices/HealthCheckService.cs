using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Api.BackgroundServices;

/// <summary>
/// Periodically checks running containers via IDockerController/IHealthChecker,
/// updates status, and logs via ILogStore.
/// </summary>
public sealed class HealthCheckService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<HealthCheckService> _logger;

    public HealthCheckService(IServiceProvider services, ILogger<HealthCheckService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HealthCheckService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _services.CreateAsyncScope();
                var docker = scope.ServiceProvider.GetRequiredService<IDockerController>();
                var healthChecker = scope.ServiceProvider.GetRequiredService<IHealthChecker>();
                var logStore = scope.ServiceProvider.GetRequiredService<ILogStore>();
                var settingsStore = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
                var registry = scope.ServiceProvider.GetRequiredService<IContainerRegistry>();

                var settings = await settingsStore.GetAsync(stoppingToken);
                var interval = TimeSpan.FromSeconds(Math.Max(settings.HealthCheckInterval, 5));

                var containers = await docker.ListContainersAsync(stoppingToken);
                var registeredContainers = await registry.ListAllAsync(stoppingToken);
                var managedIds = new HashSet<string>(
                    registeredContainers
                        .Where(r => r.RuntimeContainerId is not null)
                        .Select(r => r.RuntimeContainerId!)
                );

                foreach (var container in containers.Where(c => c.Status == ContainerStatus.Running && c.Port.HasValue && managedIds.Contains(c.Id)))
                {
                    try
                    {
                        var healthy = await healthChecker.CheckAsync(container.Port!.Value, stoppingToken);
                        if (!healthy)
                        {
                            logStore.Enqueue(LogLevel.Warn, "HealthCheck",
                                $"Container {container.Id[..12]} (model: {container.ModelName}) is unhealthy");
                        }
                    }
                    catch (Exception ex)
                    {
                        logStore.Enqueue(LogLevel.Error, "HealthCheck",
                            $"Health check failed for container {container.Id[..12]}: {ex.Message}");
                    }
                }

                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HealthCheckService error");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        _logger.LogInformation("HealthCheckService stopped");
    }
}
