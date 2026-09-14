using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Api.Services;
using Unswarm.Core.Contracts;
using Unswarm.Core.Helpers;
using Unswarm.Core.Models;

namespace Unswarm.Api.Controllers;

/// <summary>
/// Management of router profiles. A router profile defines an ordered,
/// prioritised list of model ids (cloud or local) that the inference proxy
/// can try in sequence.
/// </summary>
/// <remarks>
/// POST   /api/router-profiles           — Create a router profile
/// GET    /api/router-profiles           — List all router profiles
/// GET    /api/router-profiles/{id}      — Get a router profile by id
/// PUT    /api/router-profiles/{id}      — Update a router profile
/// DELETE /api/router-profiles/{id}      — Delete a router profile
/// PATCH  /api/router-profiles/{id}/active-entry      — Set active entry
/// PATCH  /api/router-profiles/{id}/thinking-effort   — Set thinking effort override
/// GET    /api/router-profiles/status   — Active request counts per profile
/// </remarks>
[ApiController]
[Route("api/router-profiles")]
[Authorize(Policy = "ControlPlaneAccess")]
public sealed class RouterProfileController : ControllerBase
{
    private readonly IRouterProfileStore _profiles;
    private readonly ILogger<RouterProfileController> _logger;
    private readonly RouterProfileActivityTracker _activityTracker;

    public RouterProfileController(
        IRouterProfileStore profiles,
        ILogger<RouterProfileController> logger,
        RouterProfileActivityTracker activityTracker)
    {
        _profiles = profiles;
        _logger = logger;
        _activityTracker = activityTracker;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRouterProfileRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(LocalizedError.Create("routerProfiles.nameRequired"));

        var entries = (request.Entries ?? [])
            .Select(e => new RouterProfileEntry
            {
                ModelId = e.ModelId,
                Priority = e.Priority,
                IsEnabled = e.IsEnabled,
                ThinkingEffortOverride = e.ThinkingEffortOverride,
            })
            .ToList();

        var profile = new RouterProfile
        {
            Id = string.Empty,
            Name = request.Name.Trim(),
            Mode = request.Mode,
            Entries = entries,
        };

        try
        {
            var created = await _profiles.CreateAsync(profile, ct);
            _logger.LogInformation(
                "Router profile created: name={Name}, mode={Mode}, entries={EntriesCount}",
                created.Name, created.Mode, created.Entries.Count);
            return Ok(MapToDto(created));
        }
        catch (InvalidOperationException)
        {
            return Conflict(LocalizedError.Create("routerProfiles.alreadyExists", new { name = request.Name }));
        }
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var items = await _profiles.ListAsync(ct);
        return Ok(items.Select(MapToDto));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var item = await _profiles.GetAsync(id, ct);
        return item is null
            ? NotFound(LocalizedError.Create("routerProfiles.notFound"))
            : Ok(MapToDto(item));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateRouterProfileRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(LocalizedError.Create("routerProfiles.nameRequired"));

        var entries = request.Entries
            .Select(e => new RouterProfileEntry
            {
                ModelId = e.ModelId,
                Priority = e.Priority,
                IsEnabled = e.IsEnabled,
                ThinkingEffortOverride = e.ThinkingEffortOverride,
            })
            .ToList();

        var profile = new RouterProfile
        {
            Id = id,
            Name = request.Name.Trim(),
            Mode = request.Mode,
            Entries = entries,
        };

        try
        {
            var old = await _profiles.GetAsync(id, ct);
            var oldActiveModelId = old?.ActiveModelId;
            var updated = await _profiles.UpdateAsync(id, profile, ct);
            _logger.LogInformation(
                "Router profile updated: name={Name}, mode={Mode}, entries={EntriesCount}, activeModelId={OldActive}→{NewActive}",
                updated.Name, updated.Mode, updated.Entries.Count, oldActiveModelId, updated.ActiveModelId);
            return Ok(MapToDto(updated));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(LocalizedError.Create("routerProfiles.notFound"));
        }
        catch (InvalidOperationException)
        {
            return Conflict(LocalizedError.Create("routerProfiles.alreadyExists", new { name = request.Name }));
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        try
        {
            var old = await _profiles.GetAsync(id, ct);
            await _profiles.DeleteAsync(id, ct);
            _logger.LogInformation(
                "Router profile deleted: name={Name}", old?.Name ?? id);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound(LocalizedError.Create("routerProfiles.notFound"));
        }
    }

    [HttpPatch("{id}/active-entry")]
    public async Task<IActionResult> SetActiveEntry(string id, [FromBody] SetActiveEntryRequest request, CancellationToken ct)
    {
        try
        {
            var old = await _profiles.GetAsync(id, ct);
            var oldActiveModelId = old?.ActiveModelId;
            await _profiles.SetActiveModelIdAsync(id, request.ActiveModelId, ct);
            var profile = await _profiles.GetAsync(id, ct);
            _logger.LogInformation(
                "Router profile active entry changed: name={Name}, activeModelId={OldActive}→{NewActive}",
                profile!.Name, oldActiveModelId, profile.ActiveModelId);
            return Ok(MapToDto(profile));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpPatch("{id}/thinking-effort")]
    public async Task<IActionResult> SetThinkingEffort(string id, [FromBody] SetThinkingEffortRequest request, CancellationToken ct)
    {
        try
        {
            var profile = await _profiles.SetThinkingEffortAsync(id, request.ModelId, request.ThinkingEffortOverride, ct);
            _logger.LogInformation(
                "Router profile thinking effort changed: name={Name}, modelId={ModelId}, thinkingEffortOverride={Override}",
                profile.Name, request.ModelId, request.ThinkingEffortOverride ?? "(cleared)");
            return Ok(MapToDto(profile));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(LocalizedError.Create("routerProfiles.notFound"));
        }
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(_activityTracker.GetActiveByProfile());
    }

    private static RouterProfileDto MapToDto(RouterProfile profile) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        Mode = profile.Mode,
        Entries = profile.Entries
            .Select(e => new RouterProfileEntryDto
            {
                ModelId = e.ModelId,
                Priority = e.Priority,
                IsEnabled = e.IsEnabled,
                ThinkingEffortOverride = e.ThinkingEffortOverride,
            })
            .ToList(),
        ActiveModelId = profile.ActiveModelId,
        CreatedAt = profile.CreatedAt,
        UpdatedAt = profile.UpdatedAt,
    };
}
