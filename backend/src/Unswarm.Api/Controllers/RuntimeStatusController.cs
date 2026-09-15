using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Core.Contracts;

namespace Unswarm.Api.Controllers;

/// <summary>
/// Server-Sent Events endpoint for real-time runtime status updates.
/// Pushes container/script status changes as they arrive from agent telemetry.
/// </summary>
[ApiController]
[Authorize]
public sealed class RuntimeStatusController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly IRuntimeStatusBroadcaster _broadcaster;

    public RuntimeStatusController(IRuntimeStatusBroadcaster broadcaster)
    {
        _broadcaster = broadcaster;
    }

    /// <summary>
    /// SSE stream of runtime status changes. Each event is a JSON object:
    /// { agentName, containers: [...], scripts: [...], timestamp }.
    /// Server pushes only; client disconnect terminates the stream.
    /// </summary>
    [HttpGet("/api/runtime-status/stream")]
    public async Task Stream(CancellationToken ct)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        using var subscription = _broadcaster.Subscribe();

        try
        {
            await foreach (var evt in subscription.Reader.ReadAllAsync(ct))
            {
                var json = JsonSerializer.Serialize(evt, JsonOptions);
                await Response.WriteAsync($"data: {json}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown: client disconnected or request aborted.
        }
    }
}
