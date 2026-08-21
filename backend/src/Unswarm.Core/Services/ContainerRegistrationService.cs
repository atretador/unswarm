using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services.Remote;

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
    private readonly HostScriptRuntimeController? _scriptController;

    public ContainerRegistrationService(
        IContainerRegistry registry,
        IDockerControllerRouter router,
        IHealthChecker healthChecker,
        ModelDiscoveryService discoveryService,
        IModelRegistry modelRegistry,
        IClock clock,
        ILogger<ContainerRegistrationService> logger,
        TimeSpan? remoteHealthTimeout = null,
        TimeSpan? remoteHealthPollInterval = null,
        HostScriptRuntimeController? scriptController = null)
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
        _scriptController = scriptController;
    }

    public async Task<RegisteredRuntimeWithModels> RegisterAsync(ContainerRegistrationRequest request, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var containerId = Guid.NewGuid().ToString("N");

        var agent = string.IsNullOrWhiteSpace(request.Agent) ? "host" : request.Agent.Trim();

        // Validate agent-hosted script targets are connected
        if (request.RuntimeKind == RuntimeKind.Script && !string.Equals(agent, "host", StringComparison.OrdinalIgnoreCase))
        {
            var controller = GetController(new RegisteredRuntime { Id = "validate", Image = request.Image, Agent = agent });
            if (controller is not RemoteAgentDockerController)
                throw new InvalidOperationException($"Agent '{agent}' does not have a connected RemoteAgentDockerController");
        }

        // Validate script launcher path upfront
        if (request.RuntimeKind == RuntimeKind.Script)
        {
            if (string.IsNullOrWhiteSpace(request.LauncherPath))
                throw new ArgumentException("LauncherPath is required for Script runtimes");

            // Remote scripts don't have a local file path to validate
            if (string.Equals(agent, "host", StringComparison.OrdinalIgnoreCase) && !File.Exists(request.LauncherPath))
                throw new ArgumentException($"Launcher script not found: {request.LauncherPath}");
        }

        var container = new RegisteredRuntime
        {
            Id = containerId,
            DisplayName = string.IsNullOrEmpty(request.DisplayName) ? request.Image : request.DisplayName,
            Image = request.Image,
            ContainerPort = request.ContainerPort,
            RuntimeKind = request.RuntimeKind,
            LauncherPath = request.LauncherPath,
            GpuDevices = request.GpuDevices,
            MemoryLimitMb = request.MemoryLimitMb,
            ExtraLabels = request.ExtraLabels,
            Agent = agent,
            CanRunAlongWith = request.CanRunAlongWith ?? [],
            Status = ContainerRegistrationStatus.Registered,
            CreatedAt = now,
            UpdatedAt = now
        };

        container = await _registry.CreateAsync(container, ct).ConfigureAwait(false);
        _logger.LogInformation("Registered {Kind} {Id} for image {Image}", container.RuntimeKind, containerId, request.Image);

        if (container.RuntimeKind == RuntimeKind.Script)
        {
            return await StartAndDiscoverScriptAsync(container, ct).ConfigureAwait(false);
        }

        return await StartAndDiscoverAsync(container, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the runtime container for an already-registered container (e.g. after it
    /// was stopped or OOM-killed) and waits for it to become healthy. Models are NOT
    /// re-discovered — the existing mappings from the initial registration are returned.
    /// On any start/health failure the container is persisted with Status=Error and the
    /// errored state is returned (consistent with RediscoverAsync's semantics).
    /// </summary>
    public async Task<RegisteredRuntimeWithModels> StartAsync(string registeredContainerId, CancellationToken ct = default)
    {
        var container = await _registry.GetAsync(registeredContainerId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Registered container {registeredContainerId} not found");

        _logger.LogInformation("Starting registered container {Id} (image {Image}) on agent {Agent}",
            registeredContainerId, container.Image, container.Agent);

        try
        {
            // Coexistence gate: before anything is started, stop every running
            // runtime on the same agent/host that is not in this runtime's allow
            // list, and confirm each one actually stopped. Purely allow-list based —
            // port mappings play no role here.
            await EnforceCoexistenceAsync(container, ct).ConfigureAwait(false);

            if (container.RuntimeKind == RuntimeKind.Script)
            {
                return await StartScriptAsync(container, ct).ConfigureAwait(false);
            }

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

            if (!mappedPort.HasValue && isRemote)
            {
                return await FailAsync(container, "Could not determine mapped port", ct).ConfigureAwait(false);
            }

            if (!mappedPort.HasValue)
            {
                // Host container without a published port binding (e.g. host
                // networking): fall back to the declared container port instead of
                // failing the start — the runtime is still reachable on that port.
                mappedPort = container.ContainerPort;
                _logger.LogInformation(
                    "No published port binding on container {Id}; using declared port {Port}",
                    registeredContainerId, mappedPort);
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
                await _healthChecker.WaitForReadyAsync(mappedPort.Value, 120, ct).ConfigureAwait(false);
            }

            container = await _registry.UpdateAsync(registeredContainerId, container with
            {
                Status = ContainerRegistrationStatus.Ready,
                UpdatedAt = _clock.UtcNow
                // LastDiscoveredAt intentionally untouched: it records the last model
                // discovery, not the last start.
            }, ct).ConfigureAwait(false);

            return new RegisteredRuntimeWithModels
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

    public async Task<RegisteredRuntimeWithModels> RediscoverAsync(string registeredContainerId, CancellationToken ct = default)
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

            return new RegisteredRuntimeWithModels
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

        return new RegisteredRuntimeWithModels
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
        if (container.RuntimeKind == RuntimeKind.Script)
        {
            var isHost = string.Equals(container.Agent, "host", StringComparison.OrdinalIgnoreCase);
            if (!isHost && container.RuntimeProcessId.HasValue)
            {
                // Agent-hosted script: stop via RemoteAgentDockerController by PID
                var controller = GetController(container);
                if (controller is RemoteAgentDockerController remoteController)
                {
                    _logger.LogInformation("Stopping agent script runtime {Id} (PID {Pid}) on agent {Agent}", id, container.RuntimeProcessId.Value, container.Agent);
                    await remoteController.StopScriptAsync(container.RuntimeProcessId.Value, ct).ConfigureAwait(false);
                }
            }
            else if (isHost && _scriptController is not null && container.RuntimeProcessId.HasValue)
            {
                _logger.LogInformation("Stopping script runtime {Id} (PID {Pid})", id, container.RuntimeProcessId.Value);
                await _scriptController.StopScriptAsync(id, ct).ConfigureAwait(false);
            }
        }
        else if (container.RuntimeContainerId is not null)
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
                        SourceRuntimeId = null,
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
    private IDockerController GetController(RegisteredRuntime container)
    {
        var isHost = string.IsNullOrWhiteSpace(container.Agent)
            || string.Equals(container.Agent, ExecutionTarget.HostId, StringComparison.OrdinalIgnoreCase);
        return _router.GetController(isHost ? ExecutionTarget.HostId : ExecutionTarget.ForAgent(container.Agent!).Id);
    }

    private async Task<RegisteredRuntimeWithModels> StartAndDiscoverAsync(RegisteredRuntime container, CancellationToken ct)
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

            if (!mappedPort.HasValue)
            {
                // Host container without a published port binding (e.g. host
                // networking): fall back to the declared container port.
                mappedPort = container.ContainerPort;
                _logger.LogInformation(
                    "No published port binding on container {Id}; using declared port {Port}",
                    container.Id, mappedPort);
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
                    await _healthChecker.WaitForReadyAsync(mappedPort.Value, 120, ct).ConfigureAwait(false);
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

    private async Task<RegisteredRuntimeWithModels> DiscoverAndRegisterModelsAsync(RegisteredRuntime container, CancellationToken ct)
    {
        if (!container.MappedPort.HasValue)
        {
            return new RegisteredRuntimeWithModels
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

        return new RegisteredRuntimeWithModels
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
            SourceRuntimeId = registeredContainerId,
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
            SourceRuntimeId = registeredContainerId,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = now
        };
        return await _modelRegistry.UpdateAsync(discoveredModel.ModelId, modelDef, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Script-specific registration: start the script process, wait for health, discover models.
    /// Supports both host scripts (via HostScriptRuntimeController) and agent-hosted scripts
    /// (via RemoteAgentDockerController).
    /// </summary>
    private async Task<RegisteredRuntimeWithModels> StartAndDiscoverScriptAsync(RegisteredRuntime container, CancellationToken ct)
    {
        try
        {
            // Validate launcher path
            if (string.IsNullOrWhiteSpace(container.LauncherPath))
                return await FailAsync(container, "LauncherPath is required for Script runtimes", ct).ConfigureAwait(false);

            var isHost = string.Equals(container.Agent, "host", StringComparison.OrdinalIgnoreCase);

            // Host scripts: validate local file exists
            if (isHost && !File.Exists(container.LauncherPath))
                return await FailAsync(container, $"Launcher script not found: {container.LauncherPath}", ct).ConfigureAwait(false);

            container = await _registry.UpdateAsync(container.Id, container with
            {
                Status = ContainerRegistrationStatus.Starting
            }, ct).ConfigureAwait(false);

            int pid;
            if (!isHost)
            {
                // Agent-hosted script: start via RemoteAgentDockerController
                var controller = GetController(container);
                if (controller is not RemoteAgentDockerController remoteController)
                    return await FailAsync(container, $"Agent '{container.Agent}' does not have a connected RemoteAgentDockerController", ct).ConfigureAwait(false);

                pid = await remoteController.StartScriptAsync(container.LauncherPath!, container.ContainerPort, ct).ConfigureAwait(false);
            }
            else
            {
                // Host script: start via HostScriptRuntimeController
                if (_scriptController is null)
                    return await FailAsync(container, "HostScriptRuntimeController not available", ct).ConfigureAwait(false);

                var startResult = await _scriptController.StartScriptAsync(
                    container.Id, container.LauncherPath!, container.ContainerPort, ct).ConfigureAwait(false);

                if (startResult.ErrorMessage is not null)
                    return await FailAsync(container, startResult.ErrorMessage, ct).ConfigureAwait(false);

                pid = startResult.Pid ?? 0;
            }

            // Script MappedPort = declared ContainerPort (no docker inspect needed)
            var mappedPort = container.ContainerPort;

            container = await _registry.UpdateAsync(container.Id, container with
            {
                RuntimeProcessId = pid,
                MappedPort = mappedPort
            }, ct).ConfigureAwait(false);

            // Wait for health
            await _healthChecker.WaitForReadyAsync(mappedPort, 120, ct).ConfigureAwait(false);

            container = await _registry.UpdateAsync(container.Id, container with
            {
                Status = ContainerRegistrationStatus.Healthy
            }, ct).ConfigureAwait(false);

            // Discover models
            return await DiscoverAndRegisterModelsAsync(container, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register and start script {Id}", container.Id);
            return await FailAsync(container, ex.Message, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Script-specific start (no re-discovery): start script, wait for health, return existing models.
    /// Supports both host scripts (via HostScriptRuntimeController) and agent-hosted scripts
    /// (via RemoteAgentDockerController).
    /// </summary>
    private async Task<RegisteredRuntimeWithModels> StartScriptAsync(RegisteredRuntime container, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(container.LauncherPath))
            return await FailAsync(container, "LauncherPath is required for Script runtimes", ct).ConfigureAwait(false);

        var isHost = string.Equals(container.Agent, "host", StringComparison.OrdinalIgnoreCase);

        // Host scripts: validate local file exists
        if (isHost && !File.Exists(container.LauncherPath))
            return await FailAsync(container, $"Launcher script not found: {container.LauncherPath}", ct).ConfigureAwait(false);

        container = await _registry.UpdateAsync(container.Id, container with
        {
            Status = ContainerRegistrationStatus.Starting,
            ErrorMessage = null
        }, ct).ConfigureAwait(false);

        int pid;
        try
        {
            if (!isHost)
            {
                // Agent-hosted script: start via RemoteAgentDockerController
                var controller = GetController(container);
                if (controller is not RemoteAgentDockerController remoteController)
                    return await FailAsync(container, $"Agent '{container.Agent}' does not have a connected RemoteAgentDockerController", ct).ConfigureAwait(false);

                pid = await remoteController.StartScriptAsync(container.LauncherPath!, container.ContainerPort, ct).ConfigureAwait(false);
            }
            else
            {
                // Host script: start via HostScriptRuntimeController
                if (_scriptController is null)
                    return await FailAsync(container, "HostScriptRuntimeController not available", ct).ConfigureAwait(false);

                var startResult = await _scriptController.StartScriptAsync(
                    container.Id, container.LauncherPath!, container.ContainerPort, ct).ConfigureAwait(false);

                if (startResult.ErrorMessage is not null)
                    return await FailAsync(container, startResult.ErrorMessage, ct).ConfigureAwait(false);

                pid = startResult.Pid ?? 0;
            }
        }
        catch (Exception ex)
        {
            return await FailAsync(container, $"Script start failed: {ex.Message}", ct).ConfigureAwait(false);
        }

        var mappedPort = container.ContainerPort;

        container = await _registry.UpdateAsync(container.Id, container with
        {
            RuntimeProcessId = pid,
            MappedPort = mappedPort
        }, ct).ConfigureAwait(false);

        await _healthChecker.WaitForReadyAsync(mappedPort, 120, ct).ConfigureAwait(false);

        container = await _registry.UpdateAsync(container.Id, container with
        {
            Status = ContainerRegistrationStatus.Ready,
            UpdatedAt = _clock.UtcNow
        }, ct).ConfigureAwait(false);

        return new RegisteredRuntimeWithModels
        {
            Container = container,
            DiscoveredModels = await LoadModelsForContainerAsync(container.Id, ct).ConfigureAwait(false)
        };
    }

    /// <summary>
    /// Stops every running runtime on the target's agent/host that is not allowed to
    /// coexist with <paramref name="target"/> per its <see cref="RegisteredRuntime.CanRunAlongWith"/>
    /// list, and waits until each one is fully stopped before returning.
    /// Compatibility is decided purely by allow-list membership; running containers
    /// are located by runtime container id or name match regardless of published ports.
    /// An empty allow list means the target runs alone: everything else on its agent stops.
    /// </summary>
    private async Task EnforceCoexistenceAsync(RegisteredRuntime target, CancellationToken ct)
    {
        var allRuntimes = await _registry.ListAllAsync(ct).ConfigureAwait(false);
        var peers = allRuntimes
            .Where(r => !string.Equals(r.Id, target.Id, StringComparison.OrdinalIgnoreCase)
                        && SameAgent(r.Agent, target.Agent))
            .ToList();

        if (peers.Count == 0) return;

        var controller = GetController(target);

        foreach (var peer in peers)
        {
            if (IsAllowedToCoexist(target, peer)) continue;

            if (peer.RuntimeKind == RuntimeKind.Script)
            {
                await StopPeerScriptAsync(target, peer, ct).ConfigureAwait(false);
                continue;
            }

            // Locate the peer's runtime container on this agent — port-agnostic.
            var containers = await controller.ListContainersAsync(ct).ConfigureAwait(false);
            var runningPeerContainer = FindRunningRuntimeContainer(containers, peer);
            if (runningPeerContainer is null) continue; // not running → nothing to stop

            _logger.LogInformation(
                "Stopping incompatible container {ContainerId} ({Image}) — not in allow list of {Requested}",
                runningPeerContainer.Id[..Math.Min(12, runningPeerContainer.Id.Length)], peer.Image, target.Image);

            await controller.StopContainerAsync(runningPeerContainer.Id, ct).ConfigureAwait(false);

            // Confirm stopped before handing the agent/host to the requested runtime.
            if (!await WaitUntilContainerStoppedAsync(controller, runningPeerContainer.Id, ct).ConfigureAwait(false))
            {
                throw new TimeoutException(
                    $"Incompatible container '{peer.Image}' did not stop in time; aborting start of '{target.Image}'");
            }
        }
    }

    /// <summary>
    /// Stops an incompatible script runtime and relies on the process holder's
    /// synchronous confirmation: the host script controller and the remote agent
    /// both block until the process group is dead (SIGTERM → grace period →
    /// force kill) before returning. A holder that no longer tracks the script
    /// is treated as already stopped. Any other failure propagates and aborts
    /// the start.
    /// </summary>
    private async Task StopPeerScriptAsync(RegisteredRuntime target, RegisteredRuntime peer, CancellationToken ct)
    {
        if (!peer.RuntimeProcessId.HasValue) return;

        _logger.LogInformation(
            "Stopping incompatible script runtime {Id} ({Image}) — not in allow list of {Requested}",
            peer.Id, peer.Image, target.Image);

        try
        {
            if (SameAgent(peer.Agent, ExecutionTarget.HostId))
            {
                if (_scriptController is null)
                    throw new InvalidOperationException("HostScriptRuntimeController not available");
                await _scriptController.StopScriptAsync(peer.Id, ct).ConfigureAwait(false);
            }
            else
            {
                var controller = GetController(peer);
                if (controller is not RemoteAgentDockerController remoteController)
                    throw new InvalidOperationException($"Agent '{peer.Agent}' does not have a connected RemoteAgentDockerController");
                await remoteController.StopScriptAsync(peer.RuntimeProcessId.Value, ct).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("no tracked script", StringComparison.Ordinal))
        {
            // The agent restarted and lost tracking — it holds no such process,
            // so there is nothing running to stop.
            _logger.LogWarning(
                "Script runtime {Id} ({Image}) is not tracked by its holder; treating as already stopped",
                peer.Id, peer.Image);
        }
    }

    private static bool SameAgent(string? left, string? right)
    {
        static string Normalize(string? agent) =>
            string.IsNullOrWhiteSpace(agent) ? ExecutionTarget.HostId : agent.Trim();
        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Allow-list membership test: <paramref name="other"/> may coexist with
    /// <paramref name="target"/> only when its image or display name appears in the
    /// target's CanRunAlongWith list. Empty list = runs alone.
    /// </summary>
    private static bool IsAllowedToCoexist(RegisteredRuntime target, RegisteredRuntime other)
    {
        if (target.CanRunAlongWith.Count == 0) return false;
        return target.CanRunAlongWith.Any(n =>
            string.Equals(n, other.Image, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(other.DisplayName) &&
             string.Equals(n, other.DisplayName, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Finds the peer's currently-running container among the agent's containers,
    /// matching by runtime container id first, then by container name against the
    /// registered image/display name. Deliberately ignores ports.
    /// </summary>
    private static ContainerInfo? FindRunningRuntimeContainer(IReadOnlyList<ContainerInfo> containers, RegisteredRuntime peer)
    {
        foreach (var c in containers)
        {
            if (c.Status != ContainerStatus.Running) continue;

            if (!string.IsNullOrEmpty(peer.RuntimeContainerId) &&
                string.Equals(c.Id, peer.RuntimeContainerId, StringComparison.OrdinalIgnoreCase))
                return c;

            if (NameMatches(c, peer.Image)) return c;
            if (!string.IsNullOrEmpty(peer.DisplayName) && NameMatches(c, peer.DisplayName)) return c;
        }
        return null;
    }

    private static bool NameMatches(ContainerInfo container, string name) =>
        string.Equals(container.ModelName, name, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(container.ModelId, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Polls until the container reports a non-running state (or no longer exists).
    /// Returns false when it is still running after 30 seconds.
    /// </summary>
    private async Task<bool> WaitUntilContainerStoppedAsync(IDockerController controller, string containerId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var inspect = await controller.InspectContainerAsync(containerId, ct).ConfigureAwait(false);
                if (inspect is null ||
                    !string.Equals(inspect.Status, "running", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                return true; // container no longer exists
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<RegisteredRuntimeWithModels> FailAsync(RegisteredRuntime container, string errorMessage, CancellationToken ct)
    {
        container = await _registry.UpdateAsync(container.Id, container with
        {
            Status = ContainerRegistrationStatus.Error,
            ErrorMessage = errorMessage
        }, ct).ConfigureAwait(false);

        return new RegisteredRuntimeWithModels
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
        SourceRuntimeId = model.SourceRuntimeId,
        CreatedAt = model.CreatedAt,
        UpdatedAt = model.UpdatedAt
    };
}
