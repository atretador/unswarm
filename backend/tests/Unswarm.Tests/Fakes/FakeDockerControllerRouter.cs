using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// Test router mapping target ids to controller instances, with an optional
/// reachability set (defaults to all registered targets being reachable).
/// </summary>
public sealed class FakeDockerControllerRouter : IDockerControllerRouter
{
    private readonly Dictionary<string, IDockerController> _controllers;
    private readonly HashSet<string> _reachable;

    public FakeDockerControllerRouter(
        Dictionary<string, IDockerController> controllers,
        IEnumerable<string>? reachable = null)
    {
        _controllers = controllers;
        _reachable = new HashSet<string>(reachable ?? controllers.Keys, StringComparer.Ordinal);
    }

    public IDockerController GetController(string targetId)
        => _controllers.TryGetValue(targetId, out var controller)
            ? controller
            : throw new InvalidOperationException($"No controller for target {targetId}");

    public IReadOnlyList<string> GetAvailableTargets() => _controllers.Keys.ToList();

    public bool IsTargetReachable(string targetId) => _reachable.Contains(targetId);

    /// <summary>Routing hook for incoming agent messages (test stub).</summary>
    public Action<string, AgentMessage>? OnIncoming { get; set; }

    public void HandleIncomingMessage(string agentName, AgentMessage message)
        => OnIncoming?.Invoke(agentName, message);

    public void NotifyAgentDisconnected(string agentName) { }
}
