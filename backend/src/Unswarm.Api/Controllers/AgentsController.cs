using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Api.Controllers;

/// <summary>
/// Lists execution targets ("host" + connected remote agents) with enriched
/// telemetry so callers can see where models can run. Containers are filtered to
/// the registered set so unmanaged containers never surface.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class AgentsController : ControllerBase
{
    private readonly IAgentRegistry _registry;
    private readonly IDockerControllerRouter _router;
    private readonly IContainerRegistry _containerRegistry;

    public AgentsController(
        IAgentRegistry registry,
        IDockerControllerRouter router,
        IContainerRegistry containerRegistry)
    {
        _registry = registry;
        _router = router;
        _containerRegistry = containerRegistry;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var allRegistered = await _containerRegistry.ListAllAsync(ct).ConfigureAwait(false);

        var agents = new List<AgentInfo>
        {
            await BuildHostInfoAsync(allRegistered, ct).ConfigureAwait(false)
        };

        foreach (var info in _registry.ListWithInfo())
        {
            agents.Add(FilterAgentContainers(info, allRegistered));
        }

        return Ok(agents);
    }

    [HttpGet("{name}/containers")]
    public async Task<IActionResult> ListAgentContainers(string name, CancellationToken ct)
    {
        var target = string.Equals(name, ExecutionTarget.HostId, StringComparison.OrdinalIgnoreCase)
            ? ExecutionTarget.HostId
            : ExecutionTarget.ForAgent(name).Id;

        if (!_router.IsTargetReachable(target))
            return NotFound(new { error = $"Agent '{name}' is not reachable" });

        var list = await _router.GetController(target).ListContainersAsync(ct).ConfigureAwait(false);
        return Ok(list.Select(ContainerResponse.FromContainerInfo).ToList());
    }

    private async Task<AgentInfo> BuildHostInfoAsync(IReadOnlyList<RegisteredRuntime> allRegistered, CancellationToken ct)
    {
        var containers = await _router.GetController(ExecutionTarget.HostId).ListContainersAsync(ct).ConfigureAwait(false);

        return new AgentInfo
        {
            Name = ExecutionTarget.HostId,
            IsConnected = true,
            Hostname = Environment.MachineName,
            OsPlatform = Environment.OSVersion.Platform.ToString(),
            CpuCores = Environment.ProcessorCount,
            Containers = FilterRegisteredRuntimes(containers, allRegistered, ExecutionTarget.HostId).Select(ToContainerStatus).ToList()
        };
    }

    /// <summary>
    /// Applies the registered-container filter to an agent's telemetry containers.
    /// ContainerIds are matched case-insensitively against registered runtime ids;
    /// names are matched case-insensitively against registered images.
    /// </summary>
    private static AgentInfo FilterAgentContainers(AgentInfo info, IReadOnlyList<RegisteredRuntime> allRegistered)
    {
        var registry = new RegisteredContainerSet(allRegistered, info.Name);

        return new AgentInfo
        {
            Name = info.Name,
            ConnectionId = info.ConnectionId,
            ConnectedAt = info.ConnectedAt,
            LastSeen = info.LastSeen,
            IsConnected = info.IsConnected,
            DockerSocket = info.DockerSocket,
            Version = info.Version,
            Hostname = info.Hostname,
            OsPlatform = info.OsPlatform,
            GpuInfo = info.GpuInfo,
            TotalMemoryMb = info.TotalMemoryMb,
            CpuCores = info.CpuCores,
            Containers = info.Containers
                .Where(c => registry.IsRegistered(c.ContainerId, c.ModelName))
                .ToList()
        };
    }

    /// <summary>
    /// Keeps only containers that belong to the registered set for the given agent.
    /// A container is kept if its Id matches a registered RuntimeContainerId, or if
    /// its ModelName/ModelId match a registered Image (container name).
    /// </summary>
    private static IReadOnlyList<ContainerInfo> FilterRegisteredRuntimes(
        IReadOnlyList<ContainerInfo> containers,
        IReadOnlyList<RegisteredRuntime> allRegistered,
        string agentName)
    {
        var registry = new RegisteredContainerSet(allRegistered, agentName);

        return containers
            .Where(c => registry.IsRegistered(c.Id, c.ModelName, c.ModelId, c.RegisteredRuntimeId))
            .ToList();
    }

    /// <summary>
    /// Case-insensitive lookup of the registered set for one agent.
    /// Runtime-container-id evidence (the registered-id link on the container, or a
    /// container id that equals a registered RuntimeContainerId) is authoritative and
    /// always honored. A name/image match alone is weaker evidence and is only used
    /// when no registered-id link is present AND the matched registration is not in
    /// Error status (an errored registration must not leak its container).
    /// </summary>
    private sealed class RegisteredContainerSet
    {
        private readonly HashSet<string> _runtimeIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _imageNames = new(StringComparer.OrdinalIgnoreCase);
        // Map: registered container id (case-insensitive) → has registered runtime id.
        private readonly HashSet<string> _registeredWithRuntime = new(StringComparer.OrdinalIgnoreCase);

        public RegisteredContainerSet(IReadOnlyList<RegisteredRuntime> allRegistered, string agentName)
        {
            foreach (var registered in allRegistered)
            {
                if (!MatchesAgent(registered, agentName))
                    continue;

                if (!string.IsNullOrEmpty(registered.RuntimeContainerId))
                {
                    _runtimeIds.Add(registered.RuntimeContainerId);
                    _registeredWithRuntime.Add(registered.Id);
                }

                // Name/image matches are only honored for non-Error registrations.
                if (!string.IsNullOrEmpty(registered.Image) && registered.Status != ContainerRegistrationStatus.Error)
                    _imageNames.Add(registered.Image);
            }
        }

        /// <summary>Telemetry-style entry: runtime id and/or model name.</summary>
        public bool IsRegistered(string? containerId, string? modelName)
        {
            if (!string.IsNullOrEmpty(containerId) && _runtimeIds.Contains(containerId))
                return true;

            // No runtime-id evidence: fall back to the image name (non-Error only).
            return !string.IsNullOrEmpty(modelName) && _imageNames.Contains(modelName);
        }

        /// <summary>Host-list style entry: runtime id, model name/model id, and optional registry link.</summary>
        public bool IsRegistered(string? containerId, string? modelName, string? modelId, string? registeredContainerId)
        {
            // Preferred: the container carries its registered-container link — that is
            // authoritative evidence the container is managed by this agent.
            if (!string.IsNullOrEmpty(registeredContainerId))
                return _registeredWithRuntime.Contains(registeredContainerId);

            return IsRegistered(containerId, modelName)
                || (!string.IsNullOrEmpty(modelId) && _imageNames.Contains(modelId));
        }

        private static bool MatchesAgent(RegisteredRuntime registered, string agentName)
        {
            if (string.IsNullOrWhiteSpace(registered.Agent))
                return string.Equals(agentName, ExecutionTarget.HostId, StringComparison.OrdinalIgnoreCase);
            return string.Equals(registered.Agent, agentName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static AgentContainerStatus ToContainerStatus(ContainerInfo container) => new()
    {
        ContainerId = container.Id,
        ModelName = string.IsNullOrEmpty(container.ModelName) ? null : container.ModelName,
        Status = container.Status.ToString().ToLowerInvariant(),
        Port = container.Port
    };
}
