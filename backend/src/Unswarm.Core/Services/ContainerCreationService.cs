using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;

namespace Unswarm.Core.Services;

public sealed class ContainerCreationService : IContainerCreationService
{
    private readonly Func<UnswarmDbContext> _dbFactory;
    private readonly IDockerController _docker;
    private readonly IContainerRegistry _registry;
    private readonly IHealthChecker _healthChecker;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<ContainerCreationService> _logger;

    public ContainerCreationService(
        Func<UnswarmDbContext> dbFactory,
        IDockerController docker,
        IContainerRegistry registry,
        IHealthChecker healthChecker,
        ISettingsStore settingsStore,
        ILogger<ContainerCreationService> logger)
    {
        _dbFactory = dbFactory;
        _docker = docker;
        _registry = registry;
        _healthChecker = healthChecker;
        _settingsStore = settingsStore;
        _logger = logger;
    }

    public async Task<CreateContainerResult> CreateContainerAsync(
        CreateContainerRequest request, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var containerName = request.DockerParams.ContainerName
            ?? $"unswarm-{request.Name.ToLowerInvariant().Replace(" ", "-").Replace(".", "-")}";
        var runtimeId = Guid.NewGuid().ToString("N");

        _logger.LogInformation("Creating container {Name} from image {Image} (runtime {RuntimeId})",
            containerName, request.Image, runtimeId);

        try
        {
            // Name uniqueness is enforced by DockerController.CreateContainerAsync.

            // 1. Pull image (for host containers)
            if (string.Equals(request.Agent, "host", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Pulling image {Image}", request.Image);
                try
                {
                    await _docker.PullImageAsync(request.Image, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var errorDetail = JsonSerializer.Serialize(new
                    {
                        stage = "pull",
                        message = ex.Message,
                        timestamp = now
                    });
                    await PersistErrorAsync(runtimeId, request, containerName, errorDetail, null, ct).ConfigureAwait(false);
                    return new CreateContainerResult(
                        runtimeId, ContainerCreationStatus.Failed, null,
                        $"Image pull failed: {ex.Message}", errorDetail);
                }
            }

            // 3. Create container
            string? containerId = null;
            try
            {
                var config = new ContainerCreateConfig
                {
                    Image = request.Image,
                    ContainerName = containerName,
                    ContainerPort = request.DockerParams.ContainerPort,
                    HostPort = request.DockerParams.HostPort,
                    Devices = request.DockerParams.Devices,
                    Volumes = request.DockerParams.Volumes,
                    Env = request.DockerParams.Env,
                    ShmSizeMb = request.DockerParams.ShmSizeMb,
                    IpcMode = request.DockerParams.IpcMode,
                    NetworkMode = request.DockerParams.NetworkMode,
                    RestartPolicy = request.DockerParams.RestartPolicy,
                    ServerArgs = request.DockerParams.ServerArgs
                };

                var createResult = await _docker.CreateContainerAsync(config, ct).ConfigureAwait(false);
                containerId = createResult.ContainerId;

                // 4. Register in DB
                var runtime = new RegisteredRuntime
                {
                    Id = runtimeId,
                    DisplayName = request.Name,
                    Image = request.Image,
                    ContainerPort = request.DockerParams.ContainerPort,
                    RuntimeKind = RuntimeKind.Container,
                    Agent = request.Agent,
                    Status = ContainerRegistrationStatus.Registered,
                    RuntimeContainerId = containerId,
                    MappedPort = createResult.MappedPort,
                    CreationMode = CreationMode.Created,
                    CreationConfigJson = JsonSerializer.Serialize(request.DockerParams),
                    CreatedAt = now,
                    UpdatedAt = now
                };

                await _registry.CreateAsync(runtime, ct).ConfigureAwait(false);

                _logger.LogInformation("Container {Id} created and registered (runtime {RuntimeId})",
                    containerId[..12], runtimeId);

                // 5. Start the container and update status to Starting.
                // Only start for host containers — remote agents start during create_container.
                if (string.Equals(request.Agent, "host", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _docker.StartContainerAsync(containerId, ct).ConfigureAwait(false);
                        await _registry.UpdateAsync(runtimeId, runtime with
                        {
                            Status = ContainerRegistrationStatus.Starting,
                            UpdatedAt = DateTimeOffset.UtcNow
                        }, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Container {Id} created but failed to start (runtime {RuntimeId}); " +
                            "it can still be started manually via 'unswarm containers start'",
                            containerId[..12], runtimeId);
                    }
                }
                else
                {
                    // Remote agents start the container during create_container command.
                    // Update status to Starting directly.
                    await _registry.UpdateAsync(runtimeId, runtime with
                    {
                        Status = ContainerRegistrationStatus.Starting,
                        UpdatedAt = DateTimeOffset.UtcNow
                    }, ct).ConfigureAwait(false);
                }

                // 6. Trigger health check + model discovery in the background.
                // Fire-and-forget: the background task updates the registration status
                // once the container becomes healthy and models are discovered.
                _ = Task.Run(async () => await FinalizeCreationAsync(runtimeId, containerId, createResult.MappedPort, CancellationToken.None).ConfigureAwait(false));

                return new CreateContainerResult(
                    runtimeId, ContainerCreationStatus.Starting, containerId,
                    "Container created and starting", null);
            }
            catch (Exception ex)
            {
                // Capture logs if container was created
                string? errorLogs = null;
                if (containerId is not null)
                {
                    try
                    {
                        var logs = await _docker.GetContainerLogsAsync(containerId, 200, ct).ConfigureAwait(false);
                        errorLogs = string.Join("\n", logs);
                    }
                    catch { /* Best effort log capture */ }

                    // Try to remove the failed container
                    try
                    {
                        await _docker.RemoveContainerAsync(containerId, ct).ConfigureAwait(false);
                    }
                    catch { /* Best effort cleanup */ }
                }

                var errorDetail = JsonSerializer.Serialize(new
                {
                    stage = "create",
                    message = ex.Message,
                    containerLogs = errorLogs,
                    timestamp = DateTimeOffset.UtcNow
                });
                await PersistErrorAsync(runtimeId, request, containerName, errorDetail, errorLogs, ct).ConfigureAwait(false);

                return new CreateContainerResult(
                    runtimeId, ContainerCreationStatus.Failed, containerId,
                    $"Container creation failed: {ex.Message}", errorDetail);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during container creation for {Name}", request.Name);
            return new CreateContainerResult(
                runtimeId, ContainerCreationStatus.Failed, null,
                $"Unexpected error: {ex.Message}", null);
        }
    }

    public async Task<CreateContainerResult> GetCreateStatusAsync(
        string runtimeId, CancellationToken ct = default)
    {
        var runtime = await _registry.GetAsync(runtimeId, ct).ConfigureAwait(false);
        if (runtime is null)
        {
            return new CreateContainerResult(
                runtimeId, ContainerCreationStatus.Failed, null,
                "Runtime not found", null);
        }

        var status = runtime.Status switch
        {
            ContainerRegistrationStatus.Registered => ContainerCreationStatus.Creating,
            ContainerRegistrationStatus.Starting => ContainerCreationStatus.Starting,
            ContainerRegistrationStatus.Healthy => ContainerCreationStatus.Running,
            ContainerRegistrationStatus.Discovering => ContainerCreationStatus.Running,
            ContainerRegistrationStatus.Ready => ContainerCreationStatus.Running,
            ContainerRegistrationStatus.Error => ContainerCreationStatus.Failed,
            _ => ContainerCreationStatus.Creating
        };

        return new CreateContainerResult(
            runtimeId, status, runtime.RuntimeContainerId,
            runtime.ErrorMessage, runtime.ErrorDetail);
    }

    private async Task PersistErrorAsync(
        string runtimeId,
        CreateContainerRequest request,
        string containerName,
        string errorDetail,
        string? errorLogs,
        CancellationToken ct)
    {
        try
        {
            var runtime = new RegisteredRuntime
            {
                Id = runtimeId,
                DisplayName = request.Name,
                Image = request.Image,
                ContainerPort = request.DockerParams.ContainerPort,
                RuntimeKind = RuntimeKind.Container,
                Agent = request.Agent,
                Status = ContainerRegistrationStatus.Error,
                CreationMode = CreationMode.Created,
                CreationConfigJson = JsonSerializer.Serialize(request.DockerParams),
                ErrorDetail = errorDetail,
                ErrorLogs = errorLogs,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await _registry.CreateAsync(runtime, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist error for runtime {RuntimeId}", runtimeId);
        }
    }

    /// <summary>
    /// Background finalization: waits for the container to become healthy, then
    /// transitions the registration to <see cref="ContainerRegistrationStatus.Healthy"/>.
    /// Called fire-and-forget from CreateContainerAsync. Model discovery is left to
    /// the existing ContainerRegistrationService lifecycle (StartAsync / RediscoverAsync).
    /// </summary>
    private async Task FinalizeCreationAsync(
        string runtimeId, string containerId, int? mappedPort, CancellationToken ct)
    {
        try
        {
            if (!mappedPort.HasValue)
            {
                _logger.LogInformation("Container {ContainerId} (runtime {RuntimeId}) has no mapped port; " +
                    "skipping background health check", containerId[..12], runtimeId);
                return;
            }

            var settings = await _settingsStore.GetAsync(ct).ConfigureAwait(false);
            await _healthChecker.WaitForReadyAsync(mappedPort.Value, settings.HealthCheckTimeoutSeconds, ct)
                .ConfigureAwait(false);

            var runtime = await _registry.GetAsync(runtimeId, ct).ConfigureAwait(false);
            if (runtime is null || runtime.Status == ContainerRegistrationStatus.Error)
                return;

            await _registry.UpdateAsync(runtimeId, runtime with
            {
                Status = ContainerRegistrationStatus.Healthy,
                UpdatedAt = DateTimeOffset.UtcNow
            }, ct).ConfigureAwait(false);

            _logger.LogInformation("Container {ContainerId} (runtime {RuntimeId}) is healthy", containerId[..12], runtimeId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Application shutting down — no update needed.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background health check failed for container {ContainerId} (runtime {RuntimeId})",
                containerId[..12], runtimeId);
        }
    }
}
