using System.Collections.Concurrent;
using System.Text.Json;
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public const string CommandType = "command";
    public const string CommandResultType = "command_result";
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
    /// Routes an incoming agent message to its pending command TCS. Call this from the
    /// agent WebSocket receive pump (Phase 4.2) whenever a command_result arrives.
    /// </summary>
    public void HandleIncomingMessage(AgentMessage message)
    {
        if (message is null || message.Id is null)
            return;

        if (_pending.TryRemove(message.Id, out var tcs))
        {
            tcs.TrySetResult(message);
        }
        else
        {
            _logger.LogDebug("No pending command for id {CommandId}", message.Id);
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
                    RegisteredRuntimeId = GetString(element, "registeredContainerId")
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
