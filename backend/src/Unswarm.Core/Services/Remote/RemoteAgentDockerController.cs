using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using ContainerStatus = Unswarm.Core.Models.ContainerStatus;

namespace Unswarm.Core.Services.Remote;

/// <summary>
/// IDockerController that drives a remote agent over its WebSocket connection.
///
/// Commands are sent as AgentMessage { Type = "command", Id = &lt;commandId&gt;, Agent = &lt;agentName&gt;, Payload = { command, ... } }.
/// The agent replies with AgentMessage { Type = "command_result", Id = &lt;commandId&gt;, Payload = { ... } }.
/// Results are correlated via the command id using a pending-TCS dictionary, with a command timeout.
///
/// All commands operate by CONTAINER NAME (the pre-provisioned container the agent manages).
/// Wire protocol:
///   start_container      -> payload { command, image: &lt;containerName&gt;, containerPort }
///   stop/restart/inspect -> payload { command, containerId: &lt;name&gt; }
///   get_container_logs   -> payload { command, containerId: &lt;name&gt;, tailLines }
///   list_containers      -> payload { command }
///   health_check         -> payload { command, port }
///   discover_models      -> payload { command, port }
///
/// Incoming command_result messages must be routed into <see cref="HandleIncomingMessage"/>
/// by the WebSocket receive pump (wired in Phase 4.2). Until then tests feed them directly.
/// </summary>
public sealed class RemoteAgentDockerController : IRemoteDockerController
{
    private readonly string _agentName;
    private readonly IAgentRegistry _agentRegistry;
    private readonly ILogger<RemoteAgentDockerController> _logger;
    private readonly TimeSpan _commandTimeout;
    private readonly TimeSpan _inferTimeout;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AgentMessage>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TunnelStreamOperation> _pendingStreams = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public const string CommandType = "command";
    public const string CommandResultType = "command_result";
    public const string CommandChunkType = "command_chunk";
    public const string SyncRegistrationsType = "sync_registrations";
    public const int DefaultContainerPort = 8080;

    /// <summary>
    /// Per-command timeout for inference requests. Aligned with the scheduler's
    /// default RequestTimeout (120s) so a scheduler-side timeout and the agent command
    /// timeout cannot fight: the linked cancellation token propagates the scheduler
    /// timeout to the agent, and this value is the upper bound for un-cancelled calls.
    /// </summary>
    public static readonly TimeSpan DefaultInferTimeout = TimeSpan.FromSeconds(120);

    public RemoteAgentDockerController(
        string agentName,
        IAgentRegistry agentRegistry,
        ILogger<RemoteAgentDockerController>? logger = null,
        TimeSpan? commandTimeout = null,
        TimeSpan? inferTimeout = null)
    {
        _agentName = agentName;
        _agentRegistry = agentRegistry;
        _logger = logger ?? NullLogger<RemoteAgentDockerController>.Instance;
        _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(60);
        _inferTimeout = inferTimeout ?? DefaultInferTimeout;
    }

    /// <summary>Number of commands awaiting a result (test/observability aid).</summary>
    public int PendingCommandCount => _pending.Count;

    /// <summary>
    /// Fails all pending commands for this agent with an
    /// <see cref="OperationCanceledException"/>. Called when the agent disconnects
    /// so callers don't hang waiting for responses that will never arrive.
    /// </summary>
    public void FailPendingCommands(string reason = "Agent disconnected")
    {
        foreach (var kv in _pending)
        {
            if (_pending.TryRemove(kv.Key, out var tcs))
            {
                tcs.TrySetException(new OperationCanceledException(reason));
            }
        }

        foreach (var kv in _pendingStreams)
        {
            if (_pendingStreams.TryRemove(kv.Key, out var op))
            {
                op.Completion.TrySetException(new OperationCanceledException(reason));
                op.Chunks.Writer.TryComplete();
            }
        }
    }

    /// <summary>
    /// Sends a full snapshot of this agent's registered runtime set to the agent
    /// via a "sync_registrations" message. The agent gates container lifecycle
    /// commands against this set (registeredRuntimeId → container name/id), so it
    /// must be pushed on connect and whenever registrations change.
    /// Wire contract (see agent/internal/protocol/envelope.go SyncRegistrationsPayload):
    ///   { "type": "sync_registrations", "payload": { "registrations": [
    ///       { "registeredRuntimeId": "...", "containerName": "...", "containerId": "..." } ] } }
    /// Returns false when the agent is not connected or the send fails — callers
    /// skip silently; the next connect re-syncs.
    /// </summary>
    public async Task<bool> SendRegistrationSyncAsync(IReadOnlyList<AgentRuntimeRegistration> registrations, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            registrations = registrations.Select(r => new
            {
                registeredRuntimeId = r.RegisteredRuntimeId,
                containerName = r.ContainerName,
                containerId = r.ContainerId
            })
        }, JsonOptions);

        var message = new AgentMessage
        {
            Type = SyncRegistrationsType,
            Agent = _agentName,
            Payload = payload
        };

        try
        {
            return await _agentRegistry.SendAsync(_agentName, message, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Fail-safe: a broken/half-open socket must never break registration
            // flows. The agent re-syncs on its next connect.
            return false;
        }
    }

    /// <summary>
    /// Routes an incoming agent message to its pending command TCS. Call this from the
    /// agent WebSocket receive pump (Phase 4.2) whenever a command_result arrives.
    /// </summary>
    public void HandleIncomingMessage(AgentMessage message)
    {
        if (message is null || message.Id is null)
            return;

        // Streaming chunks arrive BEFORE the final command_result and must not
        // consume the pending slot — route them into the operation's channel.
        if (string.Equals(message.Type, CommandChunkType, StringComparison.Ordinal))
        {
            if (_pendingStreams.TryGetValue(message.Id, out var op))
            {
                var data = DecodeChunkPayload(message.Payload);
                if (data is not null && data.Length > 0)
                    op.Chunks.Writer.TryWrite(data);
            }
            else
            {
                _logger.LogDebug("No pending stream command for chunk id {CommandId}", message.Id);
            }
            return;
        }

        if (_pendingStreams.TryRemove(message.Id, out var streamOp))
        {
            streamOp.Completion.TrySetResult(message);
        }

        if (_pending.TryRemove(message.Id, out var tcs))
        {
            tcs.TrySetResult(message);
        }
        else if (!_pendingStreams.ContainsKey(message.Id))
        {
            _logger.LogDebug("No pending command for id {CommandId}", message.Id);
        }
    }

    private static byte[]? DecodeChunkPayload(JsonElement? payload)
    {
        if (payload is null || !payload.HasValue)
            return null;
        if (!payload.Value.TryGetProperty("data", out var dataProp) || dataProp.ValueKind != JsonValueKind.String)
            return null;
        var encoded = dataProp.GetString();
        if (string.IsNullOrEmpty(encoded))
            return Array.Empty<byte>();
        try
        {
            return Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public async Task<ContainerStartResult> StartContainerAsync(string containerName, CancellationToken ct = default)
        => await ExecuteStartAsync("start_container", containerName, DefaultContainerPort, ct).ConfigureAwait(false);

    public async Task<ContainerStartResult> StartRegisteredContainerAsync(
        string registeredContainerId,
        string image,
        int containerPort,
        string? gpuDevices,
        long memoryLimitMb,
        Dictionary<string, string> extraLabels,
        CancellationToken ct = default)
        => await ExecuteStartAsync("start_container", image, containerPort, ct).ConfigureAwait(false);

    public async Task StopContainerAsync(string idOrModel, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            command = "stop_container",
            containerId = idOrModel
        }, JsonOptions);
        await SendCommandAsync(payload, ct).ConfigureAwait(false);
    }

    public async Task<ContainerStartResult> RestartContainerAsync(string id, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            command = "restart_container",
            containerId = id
        }, JsonOptions);
        var response = await SendCommandAsync(payload, ct).ConfigureAwait(false);
        return MapStartResult(response);
    }

    public async Task<ContainerInspectResult?> InspectContainerAsync(string id, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            command = "inspect_container",
            containerId = id
        }, JsonOptions);
        var response = await SendCommandAsync(payload, ct).ConfigureAwait(false);

        var p = response.Payload;
        if (p is null || !p.HasValue)
            return null;

        return new ContainerInspectResult
        {
            Status = GetString(p.Value, "status") ?? "unknown",
            Pid = GetInt(p.Value, "pid"),
            MemoryMb = GetLong(p.Value, "memoryMb") ?? 0,
            CpuPercent = GetDouble(p.Value, "cpuPercent") ?? 0,
            UptimeSeconds = GetLong(p.Value, "uptimeSeconds") ?? 0
        };
    }

    public async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            command = "list_containers"
        }, JsonOptions);
        var response = await SendCommandAsync(payload, ct).ConfigureAwait(false);

        var result = new List<ContainerInfo>();
        var p = response.Payload;
        if (p is null || !p.HasValue)
            return result;

        if (p.Value.TryGetProperty("containers", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
            {
                result.Add(new ContainerInfo
                {
                    Id = GetString(element, "id") ?? string.Empty,
                    ModelId = GetString(element, "modelId") ?? string.Empty,
                    ModelName = GetString(element, "modelName") ?? string.Empty,
                    Status = MapStatus(GetString(element, "status")),
                    Port = GetInt(element, "port"),
                    Pid = GetInt(element, "pid"),
                    MemoryMb = GetLong(element, "memoryMb") ?? 0,
                    CpuPercent = GetDouble(element, "cpuPercent") ?? 0,
                    Uptime = GetLong(element, "uptime") ?? 0,
                    ErrorMessage = GetString(element, "errorMessage"),
                    RegisteredRuntimeId = GetString(element, "registeredRuntimeId") ?? GetString(element, "registeredContainerId")
                });
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> GetContainerLogsAsync(string id, int tailLines = 100, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            command = "get_container_logs",
            containerId = id,
            tailLines
        }, JsonOptions);
        var response = await SendCommandAsync(payload, ct).ConfigureAwait(false);

        var result = new List<string>();
        var p = response.Payload;
        if (p is null || !p.HasValue)
            return result;

        if (p.Value.TryGetProperty("logs", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                    result.Add(element.GetString() ?? string.Empty);
            }
        }

        return result;
    }

    public async Task RemoveContainerAsync(string id, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            command = "remove_container",
            containerId = id
        }, JsonOptions);
        await SendCommandAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Remote health probe (not part of IDockerController; exposed via IRemoteDockerController).
    /// </summary>
    public async Task<bool> HealthCheckAsync(int port, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            command = "health_check",
            port
        }, JsonOptions);
        var response = await SendCommandAsync(payload, ct).ConfigureAwait(false);
        return GetBool(response.Payload, "healthy") ?? false;
    }

    /// <summary>
    /// Remote model discovery (not part of IDockerController; exposed via IRemoteDockerController).
    /// The agent's discover_models returns the RAW OpenAI /v1/models body, i.e.
    ///   { "data": [ { "id": "...", "owned_by": "..." }, ... ] }
    /// Older agents may instead return a legacy flat shape:
    ///   { "models": [ { "modelId": "...", "ownedBy": "..." }, ... ] }
    /// Entries that cannot be parsed are skipped.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(int port, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            command = "discover_models",
            port
        }, JsonOptions);
        var response = await SendCommandAsync(payload, ct).ConfigureAwait(false);

        var result = new List<DiscoveredModel>();
        var p = response.Payload;
        if (p is null || !p.HasValue)
            return result;

        // Raw OpenAI shape: { data: [ { id, owned_by } ] }
        if (p.Value.TryGetProperty("data", out var dataArray) && dataArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in dataArray.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var modelId = GetString(element, "id");
                if (string.IsNullOrEmpty(modelId))
                    continue;

                result.Add(new DiscoveredModel
                {
                    ModelId = modelId,
                    OwnedBy = GetString(element, "owned_by") ?? GetString(element, "ownedBy")
                });
            }

            return result;
        }

        // Legacy flat shape: { models: [ { modelId, ownedBy } ] }
        if (p.Value.TryGetProperty("models", out var modelsArray) && modelsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in modelsArray.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var modelId = GetString(element, "modelId") ?? GetString(element, "id");
                if (string.IsNullOrEmpty(modelId))
                    continue;

                result.Add(new DiscoveredModel
                {
                    ModelId = modelId,
                    OwnedBy = GetString(element, "ownedBy") ?? GetString(element, "owned_by")
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Runs a chat-completion request against the agent's local container. The raw
    /// OpenAI response body is returned as a string. Uses a long per-command timeout
    /// (benchmark prompts take substantially longer than container operations) and
    /// propagates the caller's CancellationToken so a scheduler-side timeout cancels
    /// the pending command (and, via the Go agent, the in-flight HTTP call).
    /// </summary>
    public async Task<string> InferAsync(int port, string requestJson, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            command = "chat_completion",
            port,
            json = requestJson
        }, JsonOptions);
        var response = await SendCommandAsync(payload, _inferTimeout, ct).ConfigureAwait(false);

        var p = response.Payload;
        if (p is null || !p.HasValue)
            throw new InvalidOperationException($"Agent '{_agentName}' returned an empty chat_completion result");

        var error = GetString(p.Value, "error");
        if (error is not null)
            throw new InvalidOperationException($"Agent '{_agentName}' chat_completion failed: {error}");

        var ok = GetBool(p.Value, "ok");
        if (ok == false)
            throw new InvalidOperationException($"Agent '{_agentName}' chat_completion returned failure");

        var data = p.Value.TryGetProperty("data", out var dataProp)
            ? dataProp.ValueKind == JsonValueKind.String
                ? dataProp.GetString()
                : dataProp.GetRawText()
            : null;
        if (string.IsNullOrEmpty(data))
            throw new InvalidOperationException($"Agent '{_agentName}' chat_completion returned no data");

        return data;
    }

    /// <summary>
    /// Streaming variant of InferAsync. Sends "chat_completion_stream"; the agent
    /// forwards response body chunks as command_chunk envelopes (base64) and then
    /// exactly one final command_result. Returns immediately after the command is
    /// sent — the returned stream yields chunks as they arrive, returns 0 on clean
    /// EOF, and throws on error results or agent disconnect. Throws
    /// NotSupportedException when the agent reports an unknown command (older
    /// agent) so callers can fall back to buffered inference.
    /// </summary>
    public async Task<Stream> InferStreamAsync(int port, string requestJson, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            command = "chat_completion_stream",
            port,
            json = requestJson
        }, JsonOptions);

        var commandId = Guid.NewGuid().ToString("N");
        var op = new TunnelStreamOperation();
        _pendingStreams[commandId] = op;

        var message = new AgentMessage
        {
            Type = CommandType,
            Id = commandId,
            Agent = _agentName,
            Payload = payload
        };

        var sent = false;
        try
        {
            sent = await _agentRegistry.SendAsync(_agentName, message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _pendingStreams.TryRemove(commandId, out _);
            op.Completion.TrySetException(ex);
            op.Chunks.Writer.TryComplete();
            throw new InvalidOperationException($"Failed to send command to agent '{_agentName}': {ex.Message}", ex);
        }

        if (!sent)
        {
            _pendingStreams.TryRemove(commandId, out _);
            var ex = new InvalidOperationException($"Agent '{_agentName}' is not connected");
            op.Completion.TrySetException(ex);
            op.Chunks.Writer.TryComplete();
            throw ex;
        }

        return new AgentTunnelStream(op, _agentName, () => _pendingStreams.TryRemove(commandId, out _));
    }

    /// <summary>Lists launcher scripts available on the remote agent.</summary>
    public async Task<IReadOnlyList<AgentScriptInfo>> ListScriptsAsync(CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToElement(new { command = "list_scripts" }, JsonOptions);
        var response = await SendCommandAsync(payload, ct).ConfigureAwait(false);

        var result = new List<AgentScriptInfo>();
        var p = response.Payload;
        if (p is null || !p.HasValue)
            return result;

        if (p.Value.TryGetProperty("scripts", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var path = GetString(element, "path");
                if (string.IsNullOrEmpty(path))
                    continue;

                result.Add(new AgentScriptInfo
                {
                    Path = path,
                    Name = GetString(element, "name") ?? string.Empty
                });
            }
        }

        return result;
    }

    /// <summary>Starts a launcher script on the remote agent. Returns the PID.</summary>
    public async Task<int> StartScriptAsync(string path, int port, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            command = "start_script",
            scriptPath = path,
            scriptPort = port
        }, JsonOptions);
        var response = await SendCommandAsync(payload, ct).ConfigureAwait(false);

        var p = response.Payload;
        if (p is null || !p.HasValue)
            throw new InvalidOperationException($"Agent '{_agentName}' returned an empty start_script result");

        var error = GetString(p.Value, "error");
        if (error is not null)
            throw new InvalidOperationException($"Agent '{_agentName}' start_script failed: {error}");

        var ok = GetBool(p.Value, "ok");
        if (ok == false)
            throw new InvalidOperationException($"Agent '{_agentName}' start_script returned failure");

        return GetInt(p.Value, "pid") ?? 0;
    }

    /// <summary>Stops a launcher script on the remote agent by PID.</summary>
    public async Task StopScriptAsync(int pid, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            command = "stop_script",
            pid
        }, JsonOptions);
        var response = await SendCommandAsync(payload, ct).ConfigureAwait(false);

        var p = response.Payload;
        if (p.HasValue)
        {
            var error = GetString(p.Value, "error");
            if (error is not null)
                throw new InvalidOperationException($"Agent '{_agentName}' stop_script failed: {error}");
        }
    }

    /// <summary>Gets log lines from a launcher script on the remote agent.</summary>
    public async Task<IReadOnlyList<string>> GetScriptLogsAsync(string path, int tailLines = 100, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            command = "get_script_logs",
            scriptPath = path,
            tailLines
        }, JsonOptions);
        var response = await SendCommandAsync(payload, ct).ConfigureAwait(false);

        var result = new List<string>();
        var p = response.Payload;
        if (p is null || !p.HasValue)
            return result;

        if (p.Value.TryGetProperty("logs", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                    result.Add(element.GetString() ?? string.Empty);
            }
        }

        return result;
    }

    private async Task<ContainerStartResult> ExecuteStartAsync(string command, string containerName, int containerPort, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            command,
            image = containerName,
            containerPort
        }, JsonOptions);
        var response = await SendCommandAsync(payload, ct).ConfigureAwait(false);
        return MapStartResult(response);
    }

    private async Task<AgentMessage> SendCommandAsync(JsonElement payload, CancellationToken ct)
        => await SendCommandAsync(payload, _commandTimeout, ct).ConfigureAwait(false);

    private async Task<AgentMessage> SendCommandAsync(JsonElement payload, TimeSpan timeout, CancellationToken ct)
    {
        var commandId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<AgentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[commandId] = tcs;

        var message = new AgentMessage
        {
            Type = CommandType,
            Id = commandId,
            Agent = _agentName,
            Payload = payload
        };

        var sent = false;
        try
        {
            sent = await _agentRegistry.SendAsync(_agentName, message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(commandId, out _);
            throw new InvalidOperationException($"Failed to send command to agent '{_agentName}': {ex.Message}", ex);
        }

        if (!sent)
        {
            _pending.TryRemove(commandId, out _);
            throw new InvalidOperationException($"Agent '{_agentName}' is not connected");
        }

        try
        {
            return await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _pending.TryRemove(commandId, out _);
            throw new TimeoutException($"Command {commandId} to agent '{_agentName}' timed out after {timeout.TotalSeconds:0}s");
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled (e.g. scheduler RequestTimeout) — clean up the pending
            // slot and propagate so the caller can act on the cancellation.
            _pending.TryRemove(commandId, out _);
            throw;
        }
    }

    /// <summary>
    /// One pending streaming command: chunks are buffered in an unbounded channel
    /// until the final command_result (or a fault) terminates the operation.
    /// </summary>
    internal sealed class TunnelStreamOperation
    {
        public Channel<byte[]> Chunks { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        /// <summary>Set by the final command_result, or faulted on error/disconnect.</summary>
        public TaskCompletionSource<AgentMessage> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Stream over the agent WebSocket tunnel: reads pull base64-decoded chunks from
    /// the operation's channel; the final command_result decides clean EOF vs error.
    /// Exposes <see cref="Drained"/> like HttpResponseMessageStream: completes when
    /// the body has been fully consumed, disposed, or faulted.
    /// </summary>
    internal sealed class AgentTunnelStream : Stream
    {
        private readonly ChannelReader<byte[]> _reader;
        private readonly TaskCompletionSource<AgentMessage> _completion;
        private readonly string _agentName;
        private readonly Action _cleanup;
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private byte[]? _currentChunk;
        private int _chunkOffset;
        private bool _eof;
        private Exception? _error;
        private bool _disposed;

        public AgentTunnelStream(TunnelStreamOperation op, string agentName, Action cleanup)
        {
            _reader = op.Chunks.Reader;
            _completion = op.Completion;
            _agentName = agentName;
            _cleanup = cleanup;
        }

        /// <summary>Completes when the stream reaches EOF, is disposed, or faults.</summary>
        public Task Drained => _drained.Task;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AgentTunnelStream));
            if (buffer.Length == 0) return 0;

            while (true)
            {
                if (_eof)
                {
                    SignalDrained();
                    return 0;
                }
                if (_error is not null)
                {
                    SignalDrained();
                    throw new IOException($"Agent '{_agentName}' tunnel stream failed", _error);
                }

                // Serve leftover bytes from a partially consumed chunk first.
                if (_currentChunk is not null && _chunkOffset < _currentChunk.Length)
                {
                    var n = Math.Min(buffer.Length, _currentChunk.Length - _chunkOffset);
                    _currentChunk.AsMemory(_chunkOffset, n).CopyTo(buffer);
                    _chunkOffset += n;
                    if (_chunkOffset >= _currentChunk.Length)
                    {
                        _currentChunk = null;
                        _chunkOffset = 0;
                    }
                    return n;
                }

                if (_reader.TryRead(out var chunk))
                {
                    if (chunk.Length == 0) continue;
                    _currentChunk = chunk;
                    _chunkOffset = 0;
                    continue;
                }

                // Wait for either more chunks or the final result — whichever first.
                var waitRead = _reader.WaitToReadAsync(ct).AsTask();
                var finished = await Task.WhenAny(waitRead, _completion.Task).ConfigureAwait(false);
                if (finished == _completion.Task)
                {
                    // Throws OperationCanceledException when faulted (disconnect).
                    var final = await _completion.Task.ConfigureAwait(false);
                    EvaluateFinal(final);
                    continue;
                }
                if (!await waitRead.ConfigureAwait(false))
                {
                    // Channel closed without a final result — treat as clean EOF.
                    _eof = true;
                }
            }
        }

        private void EvaluateFinal(AgentMessage final)
        {
            var p = final.Payload;
            if (p is null || !p.HasValue)
            {
                _error = new InvalidOperationException(
                    $"Agent '{_agentName}' chat_completion_stream returned an empty result");
                return;
            }

            var error = GetString(p.Value, "error");
            if (error is not null)
            {
                _error = error.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
                    ? new NotSupportedException($"Agent '{_agentName}' does not support chat_completion_stream")
                    : new InvalidOperationException($"Agent '{_agentName}' chat_completion_stream failed: {error}");
                return;
            }

            if (GetBool(p.Value, "ok") == false)
            {
                _error = new InvalidOperationException(
                    $"Agent '{_agentName}' chat_completion_stream returned failure");
                return;
            }

            _eof = true;
        }

        private void SignalDrained() => _drained.TrySetResult();

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException("Synchronous reads are not supported on the agent tunnel stream");

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                _cleanup();
                SignalDrained();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _cleanup();
            SignalDrained();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static ContainerStartResult MapStartResult(AgentMessage response)
    {
        var p = response.Payload;
        if (p is null || !p.HasValue)
            return new ContainerStartResult { ContainerId = string.Empty, ErrorMessage = "Empty command result" };

        var error = GetString(p.Value, "error");
        if (error is not null)
        {
            return new ContainerStartResult
            {
                ContainerId = GetString(p.Value, "containerId") ?? string.Empty,
                ErrorMessage = error
            };
        }

        return new ContainerStartResult
        {
            ContainerId = GetString(p.Value, "containerId") ?? string.Empty,
            MappedPort = GetInt(p.Value, "mappedPort")
        };
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null && value.TryGetInt32(out var n)
            ? n
            : null;

    private static long? GetLong(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null && value.TryGetInt64(out var n)
            ? n
            : null;

    private static double? GetDouble(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null && value.TryGetDouble(out var n)
            ? n
            : null;

    private static bool? GetBool(JsonElement? payload, string property)
    {
        if (payload is null || !payload.HasValue) return null;
        if (!payload.Value.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static ContainerStatus MapStatus(string? status) => (status ?? string.Empty).ToLowerInvariant() switch
    {
        "running" => ContainerStatus.Running,
        "created" or "restarting" or "starting" => ContainerStatus.Starting,
        "stopping" => ContainerStatus.Stopping,
        "dead" or "error" => ContainerStatus.Error,
        "exited" or "stopped" => ContainerStatus.Stopped,
        _ => ContainerStatus.Error
    };
}

/// <summary>
/// One entry of a sync_registrations snapshot: a registered runtime and the
/// container it owns on the target agent.
/// </summary>
public sealed record AgentRuntimeRegistration(
    string RegisteredRuntimeId,
    string? ContainerName,
    string? ContainerId);

/// <summary>
/// Lightweight descriptor for a launcher script on a remote agent.
/// </summary>
public sealed record AgentScriptInfo
{
    public required string Path { get; init; }
    public required string Name { get; init; }
}
