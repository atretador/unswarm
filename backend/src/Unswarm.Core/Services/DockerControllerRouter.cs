using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services.Remote;

namespace Unswarm.Core.Services;

/// <summary>
/// Full router: "host" maps to the local DockerController; "agent:&lt;name&gt;" maps
/// to a cached per-agent RemoteAgentDockerController. Agent targets are only usable
/// when the agent is connected (IAgentRegistry).
/// </summary>
public sealed class DockerControllerRouter : IDockerControllerRouter
{
    private readonly IDockerController _hostController;
    private readonly IAgentRegistry? _agentRegistry;
    private readonly ILogger<DockerControllerRouter> _logger;
    private readonly ConcurrentDictionary<string, RemoteAgentDockerController> _remoteControllers = new(StringComparer.Ordinal);

    public DockerControllerRouter(
        IDockerController hostController,
        IAgentRegistry? agentRegistry = null,
        ILogger<DockerControllerRouter>? logger = null)
    {
        _hostController = hostController;
        _agentRegistry = agentRegistry;
        _logger = logger ?? NullLogger<DockerControllerRouter>.Instance;
    }

    public IDockerController GetController(string targetId)
    {
        var target = ExecutionTarget.FromId(targetId);
        if (!target.IsAgent)
            return _hostController;

        if (_agentRegistry is null)
            throw new InvalidOperationException($"Agent target '{targetId}' requested but no IAgentRegistry is configured");

        return _remoteControllers.GetOrAdd(target.Id, _ => new RemoteAgentDockerController(target.AgentName!, _agentRegistry));
    }

    public IReadOnlyList<string> GetAvailableTargets()
    {
        var targets = new List<string> { ExecutionTarget.HostId };
        if (_agentRegistry is not null)
        {
            targets.AddRange(_agentRegistry.List()
                .Where(a => a.IsConnected)
                .Select(a => ExecutionTarget.ForAgent(a.Name).Id));
        }
        return targets;
    }

    public bool IsTargetReachable(string targetId)
    {
        var target = ExecutionTarget.FromId(targetId);
        if (!target.IsAgent)
            return true;

        if (_agentRegistry is null)
            return false;

        var connection = _agentRegistry.Get(target.AgentName!);
        return connection is { IsConnected: true };
    }

    /// <summary>
    /// Routes an incoming agent message (command_result) to the cached remote
    /// controller for that agent. If no controller exists yet (no command was
    /// issued through this router), the message is logged and ignored.
    /// </summary>
    public void HandleIncomingMessage(string agentName, AgentMessage message)
    {
        if (string.IsNullOrEmpty(agentName) || message is null)
            return;

        // Controllers are cached under their full target id ("agent:<name>") in GetController.
        var targetId = ExecutionTarget.ForAgent(agentName).Id;
        if (_remoteControllers.TryGetValue(targetId, out var controller))
        {
            controller.HandleIncomingMessage(message);
        }
        else
        {
            _logger.LogDebug(
                "No remote controller cached for agent {AgentName}; ignoring incoming message of type {Type}",
                agentName, message.Type);
        }
    }
}

/// <summary>
/// Host-only router used as the default when no router is supplied (keeps existing
/// single-host behavior for tests and legacy wiring).
/// </summary>
internal sealed class HostOnlyDockerControllerRouter : IDockerControllerRouter
{
    private readonly IDockerController _hostController;

    public HostOnlyDockerControllerRouter(IDockerController hostController)
    {
        _hostController = hostController;
    }

    public IDockerController GetController(string targetId) => _hostController;

    public IReadOnlyList<string> GetAvailableTargets() => [ExecutionTarget.HostId];

    public bool IsTargetReachable(string targetId) => true;

    public void HandleIncomingMessage(string agentName, AgentMessage message)
    {
        // Host-only router has no remote controllers; nothing to route.
    }
}
