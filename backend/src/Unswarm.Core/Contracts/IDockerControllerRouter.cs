using Unswarm.Core.Models;

namespace Unswarm.Core.Contracts;

/// <summary>
/// Resolves an execution target id ("host" | "agent:&lt;name&gt;") to the Docker
/// controller that manages containers on that target. Host targets are served by
/// the local DockerController; agent targets are served by per-agent
/// RemoteAgentDockerController instances talking to the agent over its WebSocket.
/// </summary>
public interface IDockerControllerRouter
{
    IDockerController GetController(string targetId);
    IReadOnlyList<string> GetAvailableTargets();
    bool IsTargetReachable(string targetId);

    /// <summary>
    /// Routes an incoming agent WebSocket message (e.g. command_result) to the
    /// cached RemoteAgentDockerController for that agent so pending commands can
    /// be correlated and completed. No-op when the agent has no cached controller.
    /// </summary>
    void HandleIncomingMessage(string agentName, AgentMessage message);

    /// <summary>
    /// Notifies the router that an agent has disconnected. Fails all pending
    /// commands for that agent so callers don't hang for the full command timeout.
    /// </summary>
    void NotifyAgentDisconnected(string agentName);
}
