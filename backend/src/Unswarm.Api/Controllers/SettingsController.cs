using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class SettingsController : ControllerBase
{
    private readonly ISettingsStore _settingsStore;

    public SettingsController(ISettingsStore settingsStore) => _settingsStore = settingsStore;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var settings = await _settingsStore.GetAsync(ct);
        return Ok(SettingsResponse.FromSettings(settings));
    }

    [Authorize(Roles = "Admin")]
    [HttpPut]
    public async Task<IActionResult> Update([FromBody] SettingsUpdateRequest request, CancellationToken ct)
    {
        var current = await _settingsStore.GetAsync(ct);

        var updated = new Settings
        {
            MaxConcurrentModels = request.MaxConcurrentModels ?? current.MaxConcurrentModels,
            DefaultModel = request.DefaultModel ?? current.DefaultModel,
            RequestTimeout = request.RequestTimeout ?? current.RequestTimeout,
            HealthCheckInterval = request.HealthCheckInterval ?? current.HealthCheckInterval,
            AutoShutdownIdle = request.AutoShutdownIdle ?? current.AutoShutdownIdle,
            IdleTimeout = request.IdleTimeout ?? current.IdleTimeout,
            LogRetention = request.LogRetention ?? current.LogRetention,
            EnableBenchmarking = request.EnableBenchmarking ?? current.EnableBenchmarking,
            PriorityMode = request.PriorityMode ?? current.PriorityMode,
            BatchDrain = request.BatchDrain ?? current.BatchDrain,
            LazyStop = request.LazyStop ?? current.LazyStop,
            MaxQueueDepth = Math.Clamp(request.MaxQueueDepth ?? current.MaxQueueDepth, 1, 10000)
        };

        var result = await _settingsStore.UpdateAsync(updated, ct);
        return Ok(SettingsResponse.FromSettings(result));
    }
}
