using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;

namespace Unswarm.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class StatsController : ControllerBase
{
    private readonly IStatsTracker _stats;
    private readonly IDockerController _docker;
    private readonly IModelRegistry _registry;

    public StatsController(IStatsTracker stats, IDockerController docker, IModelRegistry registry)
    {
        _stats = stats;
        _docker = docker;
        _registry = registry;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var summary = await _stats.GetSummaryAsync(ct);

        // Enrich with live container/model counts
        var containers = await _docker.ListContainersAsync(ct);
        var models = await _registry.ListAllAsync(ct);

        var enriched = new StatsSummaryResponse
        {
            TotalRequests = summary.TotalRequests,
            ActiveRequests = summary.ActiveRequests,
            AvgLatencyMs = summary.AvgLatencyMs,
            TotalTokensProcessed = summary.TotalTokensProcessed,
            UptimeSeconds = summary.UptimeSeconds,
            ModelsLoaded = models.Count,
            ContainersRunning = containers.Count(c => c.Status == Core.Models.ContainerStatus.Running),
            QueueDepth = summary.QueueDepth,
            RequestsPerMinute = summary.RequestsPerMinute,
            ErrorsLast24h = summary.ErrorsLast24h,
            TokensPerSecond = summary.TokensPerSecond,
            SwitchCount = summary.SwitchCount,
            LastSwitchMs = summary.LastSwitchMs,
            AvgSwitchMs = summary.AvgSwitchMs
        };

        return Ok(enriched);
    }
}
