using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Core.Services;

/// <summary>
/// Orchestrates the full lifecycle of container registration:
/// register → start → health check → discover models → ready.
/// The model list returned by the container IS the validation — no smoke inference
/// runs during registration. Benchmarks remain the optional user action afterwards.
/// Host containers are driven via the local Docker controller; containers registered
/// to a remote agent are driven through the router's RemoteAgentDockerController.
/// </summary>
public sealed class ContainerRegistrationService : IContainerRegistrationService
{
    private static readonly TimeSpan DefaultRemoteHealthTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultRemoteHealthPollInterval = TimeSpan.FromSeconds(2);

    private readonly IContainerRegistry _registry;
    private readonly IDockerControllerRouter _router;
    private readonly IHealthChecker _healthChecker;
    private readonly ModelDiscoveryService _discoveryService;
    private readonly IModelRegistry _modelRegistry;
    private readonly IClock _clock;
    private readonly ILogger<ContainerRegistrationService> _logger;
    private readonly TimeSpan _remoteHealthTimeout;
    private readonly TimeSpan _remoteHealthPollInterval;

    public ContainerRegistrationService(
        IContainerRegistry registry,
        IDockerControllerRouter router,
        IHealthChecker healthChecker,
        ModelDiscoveryService discoveryService,
        IModelRegistry modelRegistry,
        IClock clock,
        ILogger<ContainerRegistrationService> logger,
        TimeSpan? remoteHealthTimeout = null,
        TimeSpan? remoteHealthPollInterval = null)
    {
        _registry = registry;
        _router = router;
        _healthChecker = healthChecker;
        _discoveryService = discoveryService;
        _modelRegistry = modelRegistry;
        _clock = clock;
        _logger = logger;
        _remoteHealthTimeout = remoteHealthTimeout ?? DefaultRemoteHealthTimeout;
        _remoteHealthPollInterval = remoteHealthPollInterval ?? DefaultRemoteHealthPollInterval;
    }

    public async Task<RegisteredContainerWithModels> RegisterAsync(ContainerRegistrationRequest request, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var containerId = Guid.NewGuid().ToString("N");

        var container = new RegisteredContainer
        {
            Id = containerId,
            DisplayName = string.IsNullOrEmpty(request.DisplayName) ? request.Image : request.DisplayName,
            Image = request.Image,
            ContainerPort = request.ContainerPort,
            GpuDevices = request.GpuDevices,
            MemoryLimitMb = request.MemoryLimitMb,
            ExtraLabels = request.ExtraLabels,
            Agent = string.IsNullOrWhiteSpace(request.Agent) ? "host" : request.Agent.Trim(),
            CanRunAlongWith = request.CanRunAlongWith ?? [],
            Status = ContainerRegistrationStatus.Registered,
            CreatedAt = now,
            UpdatedAt = now
        };

        container = await _registry.CreateAsync(container, ct).ConfigureAwait(false);
        _logger.LogInformation("Registered container {Id} for image {Image}", containerId, request.Image);

        return await StartAndDiscoverAsync(container, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the runtime container for an already-registered container (e.g. after it
    /// was stopped or OOM-killed) and waits for it to become healthy. Models are NOT
    /// re-discovered — the existing mappings from the initial registration are returned.
    /// On any start/health failure the container is persisted with Status=Error and the
    /// errored state is returned (consistent with RediscoverAsync's semantics).
    /// </summary>
    public async Task<RegisteredContainerWithModels> StartAsync(string registeredContainerId, CancellationToken ct = default)
    {
        var container = await _registry.GetAsync(registeredContainerId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Registered container {registeredContainerId} not found");

        _logger.LogInformation("Starting registered container {Id} (image {Image}) on agent {Agent}",
            registeredContainerId, container.Image, container.Agent);

        try
        {
            var controller = GetController(container);
            var isRemote = controller is IRemoteDockerController;

            container = await _registry.UpdateAsync(registeredContainerId, container with
            {
                Status = ContainerRegistrationStatus.Starting,
                ErrorMessage = null
            }, ct).ConfigureAwait(false);

            var startResult = await controller.StartRegisteredContainerAsync(
                container.Id,
                container.Image,
                container.ContainerPort,
                gpuDevices: null,
                memoryLimitMb: 0,
                container.ExtraLabels,
                ct).ConfigureAwait(false);

            if (startResult.ErrorMessage is not null)
            {
                return await FailAsync(container, startResult.ErrorMessage, ct).ConfigureAwait(false);
            }

            // Resolve mapped port. Remote agents may omit it in the start result (or
            // return 0, which is meaningless), so fall back to listing containers and
            // matching the running container. Host starts carry the mapped port from
            // the {containerPort}/tcp inspect.
            var mappedPort = startResult.MappedPort is > 0 ? startResult.MappedPort : null;
            if (!mappedPort.HasValue && isRemote)
            {
                mappedPort = await ResolveRemoteMappedPortAsync(
                    (IRemoteDockerController)controller,
                    startResult.ContainerId,
                    container.Image,
                    container.Agent,
                    ct).ConfigureAwait(false);
            }

            if (!mappedPort.HasValue)
            {
                return await FailAsync(container, "Could not determine mapped port", ct).ConfigureAwait(false);
            }

            container = await _registry.UpdateAsync(registeredContainerId, container with
            {
                RuntimeContainerId = startResult.ContainerId,
                MappedPort = mappedPort
            }, ct).ConfigureAwait(false);

            // Wait for health (host: local health checker; remote: agent health poll).
            if (isRemote)
            {
                await WaitForRemoteHealthAsync((IRemoteDockerController)controller, mappedPort.Value, container.Agent, ct).ConfigureAwait(false);
            }
            else
            {
                await _healthChecker.WaitForReadyAsync(mappedPort.Value, ct).ConfigureAwait(false);
            }

            container = await _registry.UpdateAsync(registeredContainerId, container with
            {
                Status = ContainerRegistrationStatus.Ready,
                UpdatedAt = _clock.UtcNow
                // LastDiscoveredAt intentionally untouched: it records the last model
                // discovery, not the last start.
            }, ct).ConfigureAwait(false);

            return new RegisteredContainerWithModels
            {
                Container = container,
                DiscoveredModels = await LoadModelsForContainerAsync(registeredContainerId, ct).ConfigureAwait(false)
            };
        }
        catch (KeyNotFoundException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start registered container {Id}", registeredContainerId);
            return await FailAsync(container, ex.Message, ct).ConfigureAwait(false);
        }
    }

    public async Task<RegisteredContainerWithModels> RediscoverAsync(string registeredContainerId, CancellationToken ct = default)
    {
        var container = await _registry.GetAsync(registeredContainerId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Registered container {registeredContainerId} not found");

        if (!container.MappedPort.HasValue)
            throw new InvalidOperationException($"Container {registeredContainerId} has no mapped port; is it running?");

        _logger.LogInformation("Re-discovering models for container {Id} on port {Port}", registeredContainerId, container.MappedPort.Value);

        container = await _registry.UpdateAsync(registeredContainerId, container with
        {
            Status = ContainerRegistrationStatus.Discovering
        }, ct).ConfigureAwait(false);

        var controller = GetController(container);
        var isRemote = controller is IRemoteDockerController;
        var mappedPort = container.MappedPort!.Value;

        IReadOnlyList<DiscoveredModel> discovered;
        try
        {
            discovered = isRemote
                ? await ((IRemoteDockerController)controller).DiscoverModelsAsync(mappedPort, ct).ConfigureAwait(false)
                : await _discoveryService.DiscoverModelsAsync(mappedPort, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Transport failure (e.g. the runtime container was OOM-killed and its port
            // is dead). Surface it: mark the container Error instead of silently
            // flipping it back to Ready with zero models.
            _logger.LogError(ex, "Model discovery failed for container {ContainerId} on port {Port}",
                registeredContainerId, mappedPort);

            var errored = await _registry.UpdateAsync(registeredContainerId, container with
            {
                Status = ContainerRegistrationStatus.Error,
                ErrorMessage = $"Model discovery failed: {ex.Message}"
            }, ct).ConfigureAwait(false);

            return new RegisteredContainerWithModels
            {
                Container = errored,
                DiscoveredModels = []
            };
        }

        var existingModelIds = await _registry.GetModelIdsForContainerAsync(registeredContainerId, ct).ConfigureAwait(false);
        var existingSet = new HashSet<string>(existingModelIds);
        var discoveredSet = new HashSet<string>(discovered.Select(d => d.ModelId));

        // Mark missing models as Deprecated
        foreach (var oldModelId in existingModelIds)
        {
            if (!discoveredSet.Contains(oldModelId))
            {
                _logger.LogInformation("Model {ModelId} no longer present on container {ContainerId}; marking Deprecated", oldModelId, registeredContainerId);
                var oldModel = await _modelRegistry.GetAsync(oldModelId, ct).ConfigureAwait(false);
                if (oldModel is not null)
                {
                    await _modelRegistry.UpdateAsync(oldModelId, WithModelStatus(oldModel, ModelStatus.Deprecated), ct).ConfigureAwait(false);
                }
                await _registry.RemoveModelMappingAsync(registeredContainerId, oldModelId, ct).ConfigureAwait(false);
            }
        }

        // Add new models
        var models = new List<ModelDefinition>();
        foreach (var discoveredModel in discovered)
        {
            if (existingSet.Contains(discoveredModel.ModelId))
            {
                var existing = await _modelRegistry.GetAsync(discoveredModel.ModelId, ct).ConfigureAwait(false);
                if (existing is not null)
                    models.Add(existing);
                continue;
            }

            var modelDef = await CreateModelFromDiscoveredAsync(registeredContainerId, discoveredModel, container.MappedPort.Value, isRemote, ct).ConfigureAwait(false);
            models.Add(modelDef);
            await _registry.AddModelMappingAsync(registeredContainerId, modelDef.Id, ct).ConfigureAwait(false);
        }

        container = await _registry.UpdateAsync(registeredContainerId, container with
        {
            Status = ContainerRegistrationStatus.Ready,
            LastDiscoveredAt = _clock.UtcNow
        }, ct).ConfigureAwait(false);

        return new RegisteredContainerWithModels
        {
            Container = container,
            DiscoveredModels = models
        };
    }

    public async Task DeleteAsync(string id, bool deleteModels, CancellationToken ct = default)
    {
        var container = await _registry.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Registered container {id} not found");

        // Stop and remove the runtime container if it exists
        if (container.RuntimeContainerId is not null)
        {
            var controller = GetController(container);

            _logger.LogInformation("Stopping runtime container {RuntimeContainerId} for registered container {Id}",
                container.RuntimeContainerId[..Math.Min(12, container.RuntimeContainerId.Length)], id);
            await controller.StopContainerAsync(container.RuntimeContainerId, ct).ConfigureAwait(false);
            await controller.RemoveContainerAsync(container.RuntimeContainerId, ct).ConfigureAwait(false);
        }

        // Remove model mappings
        var modelIds = await _registry.GetModelIdsForContainerAsync(id, ct).ConfigureAwait(false);
        foreach (var modelId in modelIds)
        {
            await _registry.RemoveModelMappingAsync(id, modelId, ct).ConfigureAwait(false);

            if (deleteModels)
            {
                _logger.LogInformation("Deleting model {ModelId} from container {ContainerId}", modelId, id);
                await _modelRegistry.DeleteAsync(modelId, ct).ConfigureAwait(false);
            }
            else
            {
                // Mark as Deprecated instead
                var model = await _modelRegistry.GetAsync(modelId, ct).ConfigureAwait(false);
                if (model is not null)
                {
                    var deprecated = new ModelDefinition
                    {
                        Id = model.Id,
                        Name = model.Name,
                        Family = model.Family,
                        ParameterSize = model.ParameterSize,
                        Quantization = model.Quantization,
                        Status = ModelStatus.Deprecated,
                        ContextWindow = model.ContextWindow,
                        ContainerImage = model.ContainerImage,
                        SourceContainerId = null,
                        CreatedAt = model.CreatedAt,
                        UpdatedAt = _clock.UtcNow
                    };
                    await _modelRegistry.UpdateAsync(modelId, deprecated, ct).ConfigureAwait(false);
                }
            }
        }

        await _registry.DeleteAsync(id, ct).ConfigureAwait(false);
        _logger.LogInformation("Deleted registered container {Id}", id);
    }

    /// <summary>
    /// Resolves the controller for the container's execution target:
    /// "host" → local controller, anything else → "agent:&lt;name&gt;" via the router.
    /// Agent names are compared case-insensitively to stay consistent with
    /// ExecutionTarget.FromId (which parses "agent:&lt;name&gt;" case-insensitively).
    /// </summary>
    private IDockerController GetController(RegisteredContainer container)
    {
        var isHost = string.IsNullOrWhiteSpace(container.Agent)
            || string.Equals(container.Agent, ExecutionTarget.HostId, StringComparison.OrdinalIgnoreCase);
        return _router.GetController(isHost ? ExecutionTarget.HostId : ExecutionTarget.ForAgent(container.Agent!).Id);
    }

    private async Task<RegisteredContainerWithModels> StartAndDiscoverAsync(RegisteredContainer container, CancellationToken ct)
    {
        try
        {
            var controller = GetController(container);
            var isRemote = controller is IRemoteDockerController;

            // Step 1: Start the container via the target's controller
            container = await _registry.UpdateAsync(container.Id, container with
            {
                Status = ContainerRegistrationStatus.Starting
            }, ct).ConfigureAwait(false);

            var startResult = await controller.StartRegisteredContainerAsync(
                container.Id,
                container.Image,
                container.ContainerPort,
                gpuDevices: null,
                memoryLimitMb: 0,
                container.ExtraLabels,
                ct).ConfigureAwait(false);

            if (startResult.ErrorMessage is not null)
            {
                return await FailAsync(container, startResult.ErrorMessage, ct).ConfigureAwait(false);
            }

            // Resolve mapped port. Remote agents may omit it in the start result (or
            // return 0, which is meaningless), so fall back to listing containers and
            // matching the running container.
            var mappedPort = startResult.MappedPort is > 0 ? startResult.MappedPort : null;
            if (!mappedPort.HasValue && isRemote)
            {
                mappedPort = await ResolveRemoteMappedPortAsync(
                    (IRemoteDockerController)controller,
                    startResult.ContainerId,
                    container.Image,
                    container.Agent,
                    ct).ConfigureAwait(false);
            }

            if (!mappedPort.HasValue && isRemote)
            {
                return await FailAsync(container, "Could not determine mapped port for remote container", ct).ConfigureAwait(false);
            }

            container = await _registry.UpdateAsync(container.Id, container with
            {
                RuntimeContainerId = startResult.ContainerId,
                MappedPort = mappedPort
            }, ct).ConfigureAwait(false);

            // Step 2: Wait for health
            if (mappedPort.HasValue)
            {
                if (isRemote)
                {
                    await WaitForRemoteHealthAsync((IRemoteDockerController)controller, mappedPort.Value, container.Agent, ct).ConfigureAwait(false);
                }
                else
                {
                    await _healthChecker.WaitForReadyAsync(mappedPort.Value, ct).ConfigureAwait(false);
                }
            }

            container = await _registry.UpdateAsync(container.Id, container with
            {
                Status = ContainerRegistrationStatus.Healthy
            }, ct).ConfigureAwait(false);

            // Step 3: Discover models
            return await DiscoverAndRegisterModelsAsync(container, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register and start container {Id}", container.Id);
            return await FailAsync(container, ex.Message, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolves the remote container's mapped port by listing containers on the agent.
    /// 1. An exact Id == runtimeContainerId match wins (most reliable).
    /// 2. Otherwise fall back to a case-insensitive image-name match, but ONLY when
    ///    exactly one container matches; ambiguous matches cannot be resolved safely.
    /// </summary>
    private async Task<int?> ResolveRemoteMappedPortAsync(
        IRemoteDockerController remote,
        string runtimeContainerId,
        string image,
        string agentName,
        CancellationToken ct)
    {
        try
        {
            var containers = await remote.ListContainersAsync(ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(runtimeContainerId))
            {
                var byId = containers.FirstOrDefault(c =>
                    string.Equals(c.Id, runtimeContainerId, StringComparison.OrdinalIgnoreCase));
                if (byId is not null)
                    return byId.Port is > 0 ? byId.Port : null;
            }

            var byImage = containers
                .Where(c =>
                    (!string.IsNullOrEmpty(c.ModelName) && string.Equals(c.ModelName, image, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrEmpty(c.ModelId) && string.Equals(c.ModelId, image, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (byImage.Count == 1)
            {
                var port = byImage[0].Port;
                return port is > 0 ? port : null;
            }

            if (byImage.Count > 1)
            {
                throw new InvalidOperationException(
                    $"{byImage.Count} containers match image '{image}' on agent '{agentName}'; cannot determine runtime container");
            }

            return null;
        }
        catch (InvalidOperationException)
        {
            // Ambiguity is a hard error — propagate to the caller for Error status.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve mapped port for remote container {ContainerId}", runtimeContainerId);
            return null;
        }
    }

    /// <summary>
    /// Polls the remote agent's health probe until it reports healthy or the deadline
    /// is reached. A grace delay is applied before the first probe so cold containers
    /// have time to come up, and a probe exception is logged and tolerated (polling
    /// continues) — only running out of time fails registration.
    /// </summary>
    private async Task WaitForRemoteHealthAsync(IRemoteDockerController remote, int mappedPort, string agentName, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _remoteHealthTimeout;

        // Cold-container grace period before the first probe.
        await Task.Delay(_remoteHealthPollInterval, ct).ConfigureAwait(false);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var healthy = await remote.HealthCheckAsync(mappedPort, ct).ConfigureAwait(false);
                if (healthy)
                    return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Transient probe failure — keep polling until the deadline.
                _logger.LogWarning(ex,
                    "Health probe failed on agent '{AgentName}' port {Port}; continuing to poll until deadline",
                    agentName, mappedPort);
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;

            var delay = remaining < _remoteHealthPollInterval ? remaining : _remoteHealthPollInterval;
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        throw new TimeoutException($"Container health check timed out on agent '{agentName}'");
    }

    private async Task<RegisteredContainerWithModels> DiscoverAndRegisterModelsAsync(RegisteredContainer container, CancellationToken ct)
    {
        if (!container.MappedPort.HasValue)
        {
            return new RegisteredContainerWithModels
            {
                Container = container,
                DiscoveredModels = []
            };
        }

        container = await _registry.UpdateAsync(container.Id, container with
        {
            Status = ContainerRegistrationStatus.Discovering
        }, ct).ConfigureAwait(false);

        var controller = GetController(container);
        var isRemote = controller is IRemoteDockerController;

        var discovered = isRemote
            ? await ((IRemoteDockerController)controller).DiscoverModelsAsync(container.MappedPort!.Value, ct).ConfigureAwait(false)
            : await _discoveryService.DiscoverModelsAsync(container.MappedPort!.Value, ct).ConfigureAwait(false);

        // The model list from the container IS the validation — no smoke inference
        // runs during registration. Every discovered model is created/updated Ready.
        var models = new List<ModelDefinition>();
        foreach (var discoveredModel in discovered)
        {
            var modelDef = await CreateModelFromDiscoveredAsync(container.Id, discoveredModel, container.MappedPort.Value, isRemote, ct).ConfigureAwait(false);
            models.Add(modelDef);
        }

        foreach (var modelDef in models)
        {
            await _registry.AddModelMappingAsync(container.Id, modelDef.Id, ct).ConfigureAwait(false);
        }

        container = await _registry.UpdateAsync(container.Id, container with
        {
            Status = ContainerRegistrationStatus.Ready,
            LastDiscoveredAt = _clock.UtcNow
        }, ct).ConfigureAwait(false);

        _logger.LogInformation("Container {Id} ready with {Count} discovered models", container.Id, models.Count);

        return new RegisteredContainerWithModels
        {
            Container = container,
            DiscoveredModels = models
        };
    }

    private async Task<ModelDefinition> CreateModelFromDiscoveredAsync(
        string registeredContainerId,
        DiscoveredModel discoveredModel,
        int port,
        bool isRemote,
        CancellationToken ct)
    {
        var now = _clock.UtcNow;

        // Discovery IS validation: a model listed by the container's /v1/models
        // endpoint is Ready. No smoke inference runs during registration.
        var modelDef = new ModelDefinition
        {
            Id = discoveredModel.ModelId,
            Name = discoveredModel.ModelId,
            ContainerImage = string.Empty,
            SourceContainerId = registeredContainerId,
            Status = ModelStatus.Ready,
            CreatedAt = now,
            UpdatedAt = now
        };

        // Create or update the model, preserving existing metadata on update
        // (Family/ParameterSize/Quantization/ContextWindow are never clobbered).
        var existing = await _modelRegistry.GetAsync(discoveredModel.ModelId, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return await _modelRegistry.CreateAsync(modelDef, ct).ConfigureAwait(false);
        }

        modelDef = new ModelDefinition
        {
            Id = existing.Id,
            Name = discoveredModel.ModelId,
            Family = existing.Family,
            ParameterSize = existing.ParameterSize,
            Quantization = existing.Quantization,
            Status = ModelStatus.Ready,
            ContextWindow = existing.ContextWindow,
            ContainerImage = existing.ContainerImage,
            SourceContainerId = registeredContainerId,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = now
        };
        return await _modelRegistry.UpdateAsync(discoveredModel.ModelId, modelDef, ct).ConfigureAwait(false);
    }

    private async Task<RegisteredContainerWithModels> FailAsync(RegisteredContainer container, string errorMessage, CancellationToken ct)
    {
        container = await _registry.UpdateAsync(container.Id, container with
        {
            Status = ContainerRegistrationStatus.Error,
            ErrorMessage = errorMessage
        }, ct).ConfigureAwait(false);

        return new RegisteredContainerWithModels
        {
            Container = container,
            DiscoveredModels = []
        };
    }

    /// <summary>
    /// Loads the model definitions currently mapped to a registered container
    /// (used by StartAsync to return existing models without re-running discovery).
    /// </summary>
    private async Task<IReadOnlyList<ModelDefinition>> LoadModelsForContainerAsync(string registeredContainerId, CancellationToken ct)
    {
        var modelIds = await _registry.GetModelIdsForContainerAsync(registeredContainerId, ct).ConfigureAwait(false);
        var models = new List<ModelDefinition>();
        foreach (var modelId in modelIds)
        {
            var model = await _modelRegistry.GetAsync(modelId, ct).ConfigureAwait(false);
            if (model is not null)
                models.Add(model);
        }
        return models;
    }

    private static ModelDefinition WithModelStatus(ModelDefinition model, ModelStatus status) => new()
    {
        Id = model.Id,
        Name = model.Name,
        Family = model.Family,
        ParameterSize = model.ParameterSize,
        Quantization = model.Quantization,
        Status = status,
        ContextWindow = model.ContextWindow,
        ContainerImage = model.ContainerImage,
        SourceContainerId = model.SourceContainerId,
        CreatedAt = model.CreatedAt,
        UpdatedAt = model.UpdatedAt
    };
}
