using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
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
/// </remarks>
[ApiController]
[Route("api/router-profiles")]
[Authorize(Roles = "Admin")]
public sealed class RouterProfileController : ControllerBase
{
    private readonly IRouterProfileStore _profiles;

    public RouterProfileController(IRouterProfileStore profiles)
    {
        _profiles = profiles;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRouterProfileRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required." });

        var entries = (request.Entries ?? [])
            .Select(e => new RouterProfileEntry
            {
                ModelId = e.ModelId,
                Priority = e.Priority,
                IsEnabled = e.IsEnabled,
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
            return Ok(MapToDto(created));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
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
            ? NotFound(new { error = "Router profile not found." })
            : Ok(MapToDto(item));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateRouterProfileRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required." });

        var entries = request.Entries
            .Select(e => new RouterProfileEntry
            {
                ModelId = e.ModelId,
                Priority = e.Priority,
                IsEnabled = e.IsEnabled,
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
            var updated = await _profiles.UpdateAsync(id, profile, ct);
            return Ok(MapToDto(updated));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Router profile not found." });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        try
        {
            await _profiles.DeleteAsync(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Router profile not found." });
        }
    }

    [HttpPatch("{id}/active-entry")]
    public async Task<IActionResult> SetActiveEntry(string id, [FromBody] SetActiveEntryRequest request, CancellationToken ct)
    {
        try
        {
            await _profiles.SetActiveModelIdAsync(id, request.ActiveModelId, ct);
            var profile = await _profiles.GetAsync(id, ct);
            return Ok(MapToDto(profile!));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
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
            })
            .ToList(),
        ActiveModelId = profile.ActiveModelId,
        CreatedAt = profile.CreatedAt,
        UpdatedAt = profile.UpdatedAt,
    };
}
