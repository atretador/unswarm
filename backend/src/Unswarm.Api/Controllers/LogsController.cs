using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Api.Controllers;

[ApiController]
// Logs can contain API key names, model ids, and error details — admin-only.
[Authorize(Roles = "Admin")]
[Route("api/[controller]")]
public sealed class LogsController : ControllerBase
{
    private readonly ILogStore _logStore;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public LogsController(ILogStore logStore) => _logStore = logStore;

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? source = null,
        [FromQuery] LogLevel? level = null,
        [FromQuery] int limit = 100,
        [FromQuery] DateTimeOffset? since = null,
        CancellationToken ct = default)
    {
        var entries = await _logStore.GetHistoricalAsync(source, level, limit, since, ct);
        return Ok(entries.Select(LogEntryResponse.FromEntry).ToList());
    }

    [HttpGet("stream")]
    public async Task Stream(CancellationToken cancellationToken)
    {
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        Response.ContentType = "text/event-stream";

        await foreach (var entry in _logStore.SubscribeAsync(cancellationToken))
        {
            var json = JsonSerializer.Serialize(LogEntryResponse.FromEntry(entry), s_jsonOptions);
            await Response.WriteAsync($"data: {json}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
    }
}
