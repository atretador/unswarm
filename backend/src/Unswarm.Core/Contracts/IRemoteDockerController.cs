using Unswarm.Core.Models;
using Unswarm.Core.Services.Remote;

namespace Unswarm.Core.Contracts;

/// <summary>
/// IDockerController variant for remote agents. Adds agent-mediated health probing,
/// model discovery, and inference proxying (all tunneled over the agent WebSocket).
/// </summary>
public interface IRemoteDockerController : IDockerController
{
    Task<bool> HealthCheckAsync(int port, CancellationToken ct = default);
    Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(int port, CancellationToken ct = default);

    /// <summary>
    /// Runs a chat-completion request against the remote agent's local container.
    /// <paramref name="requestJson"/> is the raw OpenAI chat-completions body; the raw
    /// response body is returned as a string.
    /// </summary>
    Task<string> InferAsync(int port, string requestJson, CancellationToken ct = default);

    /// <summary>
    /// Streaming variant of <see cref="InferAsync"/>. Sends a "chat_completion_stream"
    /// command; the agent forwards response body chunks as command_chunk envelopes and
    /// terminates with exactly one final command_result. The returned stream yields
    /// chunks as they arrive (0 on clean EOF, throws on error/disconnect). Throws
    /// <see cref="NotSupportedException"/> when the agent does not support the
    /// streaming command (older agent) so callers can fall back to buffered inference.
    /// </summary>
    Task<Stream> InferStreamAsync(int port, string requestJson, CancellationToken ct = default);

    /// <summary>Lists launcher scripts available on the remote agent.</summary>
    Task<IReadOnlyList<AgentScriptInfo>> ListScriptsAsync(CancellationToken ct = default);

    /// <summary>Uploads a script to the remote agent's scripts directory.</summary>
    Task<AgentScriptInfo> UploadScriptAsync(string name, string content, CancellationToken ct = default);

    /// <summary>Updates an existing script on the remote agent.</summary>
    Task<AgentScriptInfo> UpdateScriptAsync(string name, string content, CancellationToken ct = default);

    /// <summary>Reads the text content of a script on the remote agent.</summary>
    Task<string> GetScriptContentAsync(string path, CancellationToken ct = default);
}
