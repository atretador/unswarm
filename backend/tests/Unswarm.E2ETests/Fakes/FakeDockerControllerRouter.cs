using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.E2ETests.Fakes;

/// <summary>
/// Test router mapping target ids to controller instances; all registered
/// targets are reachable by default.
/// </summary>
public sealed class FakeDockerControllerRouter : IDockerControllerRouter
{
    private readonly Dictionary<string, IDockerController> _controllers;

    public FakeDockerControllerRouter(Dictionary<string, IDockerController> controllers)
        => _controllers = controllers;

    public IDockerController GetController(string targetId)
        => _controllers.TryGetValue(targetId, out var controller)
            ? controller
            : throw new InvalidOperationException($"No controller for target {targetId}");

    public IReadOnlyList<string> GetAvailableTargets() => _controllers.Keys.ToList();

    public bool IsTargetReachable(string targetId) => _controllers.ContainsKey(targetId);

    public void HandleIncomingMessage(string agentName, AgentMessage message) { }

    public void NotifyAgentDisconnected(string agentName) { }
}
