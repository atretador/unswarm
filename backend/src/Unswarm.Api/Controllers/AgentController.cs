using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Api.Controllers;

// Remote-agent WebSocket channel. Only an agent-scoped API key may connect
// (the dashboard's cookie carries no scope, so it is rejected here).
[Authorize(Policy = "AgentKey")]
public sealed class AgentController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly IAgentRegistry _registry;
    private readonly ILogger<AgentController> _logger;
    private readonly IDockerControllerRouter? _router;

    public AgentController(
        IAgentRegistry registry,
        ILogger<AgentController> logger,
        IDockerControllerRouter? router = null)
    {
        _registry = registry;
        _logger = logger;
        _router = router;
    }

    [HttpGet("/ws/agent")]
    public async Task Get(CancellationToken ct)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = 400;
            return;
        }

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        await HandleConnectionAsync(socket, ct);
    }

    // Extracted for testability — core connection handling without HttpContext dependency
    public async Task HandleConnectionAsync(WebSocket socket, CancellationToken ct)
    {
        string? agentName = null;
        string? connectionId = null;

        try
        {
            // B1 + M2: Use shared camelCase options + reassembly loop for hello
            var helloJson = await ReceiveFullMessageAsync(socket, ct);
            if (helloJson is null)
                return;

            var msg = JsonSerializer.Deserialize<AgentMessage>(helloJson, JsonOptions);

            if (msg is null || msg.Type != "hello")
            {
                await SendError(socket, "First message must be type: hello", ct);
                return;
            }

            if (msg.Payload is not { } payload || !payload.TryGetProperty("name", out var nameProp))
            {
                await SendError(socket, "hello payload must include: name", ct);
                return;
            }

            agentName = nameProp.GetString();
            if (string.IsNullOrWhiteSpace(agentName))
            {
                await SendError(socket, "name cannot be empty", ct);
                return;
            }

            string? dockerSocket = payload.TryGetProperty("dockerSocket", out var ds) ? ds.GetString() : null;
            string? version = payload.TryGetProperty("version", out var v) ? v.GetString() : null;

            // M1: Generate unique connectionId
            connectionId = Guid.NewGuid().ToString("N");

            var connection = new AgentConnection
            {
                Name = agentName,
                ConnectionId = connectionId,
                ConnectedAt = DateTimeOffset.UtcNow,
                LastSeen = DateTimeOffset.UtcNow,
                IsConnected = true,
                DockerSocket = dockerSocket,
                Version = version
            };

            _registry.Register(agentName, connection, socket);

            // B1 + m3: Use shared camelCase options for hello ack
            var ackPayload = JsonSerializer.SerializeToElement(new { ok = true }, JsonOptions);
            await SendAsync(socket, new AgentMessage { Type = "hello", Payload = ackPayload }, ct);

            await ReadLoop(socket, agentName, ct);
        }
        catch (Exception ex)
        {
            // m1: Log exceptions instead of swallowing silently
            _logger.LogWarning(ex, "WebSocket connection error for agent {AgentName}", agentName ?? "(unknown)");
        }
        finally
        {
            // M1: Pass connectionId for safe unregister
            if (agentName is not null && connectionId is not null)
            {
                _registry.Unregister(agentName, connectionId);
            }

            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Server closing",
                        CancellationToken.None);
                }
                catch { /* best effort */ }
            }
        }
    }

    // M2: Reassembly loop — accumulate bytes until EndOfMessage
    private const int MaxMessageSize = 1024 * 1024; // 1 MB

    private static async Task<string?> ReceiveFullMessageAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();

        while (!ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            if (result.Count > 0)
                await ms.WriteAsync(buffer.AsMemory(0, result.Count), ct);

            if (ms.Length > MaxMessageSize)
                throw new InvalidOperationException($"WebSocket message exceeded {MaxMessageSize} bytes");

            if (result.EndOfMessage)
                break;
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private async Task ReadLoop(WebSocket socket, string agentName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            AgentMessage? msg;
            try
            {
                // M2: Reassembly loop for read loop messages
                var json = await ReceiveFullMessageAsync(socket, ct);
                if (json is null)
                    break;

                // B1: Use shared camelCase options
                msg = JsonSerializer.Deserialize<AgentMessage>(json, JsonOptions);
            }
            catch (WebSocketException)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (msg is null)
                continue;

            var conn = _registry.Get(agentName);
            if (conn is not null)
                conn.LastSeen = DateTimeOffset.UtcNow;

            switch (msg.Type)
            {
                case "heartbeat":
                    // Ack the heartbeat
                    var ack = new AgentMessage { Type = "heartbeat", Agent = agentName, Id = msg.Id };
                    await _registry.SendAsync(agentName, ack, ct);
                    break;

                case "telemetry":
                    ParseTelemetry(conn, msg.Payload);
                    break;

                case "command_result":
                    // Route to the agent's RemoteAgentDockerController so pending
                    // commands can be correlated and completed.
                    _router?.HandleIncomingMessage(agentName, msg);
                    break;

                default:
                    // m3: Use shared camelCase options for error payload
                    var errPayload = JsonSerializer.SerializeToElement(
                        new { error = $"Unknown message type: {msg.Type}" }, JsonOptions);
                    var err = new AgentMessage
                    {
                        Type = "error",
                        Agent = agentName,
                        Id = msg.Id,
                        Payload = errPayload
                    };
                    await _registry.SendAsync(agentName, err, ct);
                    break;
            }
        }
    }

    // B1: Use shared static options for SendError
    private static async Task SendError(WebSocket socket, string error, CancellationToken ct)
    {
        var msg = new AgentMessage
        {
            Type = "error",
            Payload = JsonSerializer.SerializeToElement(new { error }, JsonOptions)
        };
        await SendAsync(socket, msg, ct);
    }

    /// <summary>
    /// Parses enriched telemetry from an agent and updates the AgentConnection so
    /// the agent's status can be surfaced through IAgentRegistry.GetInfo/ListWithInfo.
    /// </summary>
    private static void ParseTelemetry(AgentConnection? connection, JsonElement? payload)
    {
        if (connection is null || payload is null || !payload.HasValue)
            return;

        var root = payload.Value;
        if (root.TryGetProperty("hostname", out var hostname))
            connection.Hostname = hostname.ValueKind == JsonValueKind.String ? hostname.GetString() : null;

        if (root.TryGetProperty("osPlatform", out var os))
            connection.OsPlatform = os.ValueKind == JsonValueKind.String ? os.GetString() : null;

        if (root.TryGetProperty("gpuInfo", out var gpuInfo))
            connection.GpuInfo = gpuInfo.ValueKind == JsonValueKind.String ? gpuInfo.GetString() : null;

        if (root.TryGetProperty("totalMemoryMb", out var mem) && mem.ValueKind != JsonValueKind.Null && mem.TryGetInt64(out var memValue))
            connection.TotalMemoryMb = memValue;

        if (root.TryGetProperty("cpuCores", out var cores) && cores.ValueKind != JsonValueKind.Null && cores.TryGetInt32(out var coresValue))
            connection.CpuCores = coresValue;

        if (root.TryGetProperty("containers", out var containers) && containers.ValueKind == JsonValueKind.Array)
            connection.Containers = ParseContainers(containers);

        if (root.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Array)
            connection.Scripts = ParseScripts(scripts);
    }

    private static string? FormatGpuSummary(JsonElement gpuArray)
    {
        var parts = new List<string>();
        foreach (var element in gpuArray.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;

            var name = element.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;
            var memoryMb = element.TryGetProperty("memory", out var m) && m.ValueKind != JsonValueKind.Null && m.TryGetInt64(out var mem)
                ? mem
                : (long?)null;

            if (string.IsNullOrWhiteSpace(name))
                continue;

            parts.Add(memoryMb is > 0
                ? $"{name} ({FormatMemoryMb(memoryMb.Value)})"
                : name);
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string FormatMemoryMb(long memoryMb)
    {
        if (memoryMb >= 1024 && memoryMb % 1024 == 0)
            return $"{memoryMb / 1024}GB";
        return $"{memoryMb}MB";
    }

    private static List<AgentContainerStatus> ParseContainers(JsonElement containerArray)
    {
        var result = new List<AgentContainerStatus>();
        foreach (var element in containerArray.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;

            var containerId = element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(containerId))
                continue;

            string? modelName = element.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;

            string status = element.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String
                ? statusProp.GetString() ?? ""
                : "";

            int? port = element.TryGetProperty("port", out var portProp) && portProp.ValueKind != JsonValueKind.Null && portProp.TryGetInt32(out var p)
                ? p
                : null;

            result.Add(new AgentContainerStatus
            {
                ContainerId = containerId,
                ModelName = modelName,
                Status = status,
                Port = port
            });
        }

        return result;
    }

    private static List<AgentScriptStatus> ParseScripts(JsonElement scriptArray)
    {
        var result = new List<AgentScriptStatus>();
        foreach (var element in scriptArray.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;

            var path = element.TryGetProperty("path", out var pathProp) && pathProp.ValueKind == JsonValueKind.String
                ? pathProp.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(path))
                continue;

            string status = element.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String
                ? statusProp.GetString() ?? ""
                : "";

            int pid = element.TryGetProperty("pid", out var pidProp) && pidProp.ValueKind != JsonValueKind.Null && pidProp.TryGetInt32(out var pidVal)
                ? pidVal
                : 0;

            int port = element.TryGetProperty("port", out var portProp) && portProp.ValueKind != JsonValueKind.Null && portProp.TryGetInt32(out var portVal)
                ? portVal
                : 0;

            long startTime = element.TryGetProperty("startTime", out var stProp) && stProp.ValueKind != JsonValueKind.Null && stProp.TryGetInt64(out var stVal)
                ? stVal
                : 0;

            result.Add(new AgentScriptStatus
            {
                Path = path,
                PID = pid,
                Status = status,
                Port = port,
                StartTime = startTime
            });
        }

        return result;
    }

    // B1: Shared send helper using static options
    private static async Task SendAsync(WebSocket socket, AgentMessage message, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(message, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            ct);
    }
}
