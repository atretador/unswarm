using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Api.Controllers;

/// <summary>
/// Management of managed inference API keys. Admin-only. The raw secret is
/// returned only at create/rotation time and is never persisted in full.
/// </summary>
[ApiController]
// Explicit route — ASP.NET lowercases the [controller] token to "apikeys",
// which would not match the frontend wire contract at "/api/api-keys".
[Route("api/api-keys")]
[Authorize(Roles = "Admin")]
public sealed class ApiKeyController : ControllerBase
{
    private readonly IApiKeyStore _keys;

    public ApiKeyController(IApiKeyStore keys) => _keys = keys;

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateApiKeyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required." });

        // Inference-scope key creation. Agent-scoped keys are created
        // through POST api/api-keys/agent below.
        var created = await _keys.CreateAsync(request.Name.Trim(), ApiKeyScope.Inference, ct: ct);
        return Ok(Map(created));
    }

    [HttpPost("agent")]
    public async Task<IActionResult> CreateAgent([FromBody] CreateApiKeyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required." });

        // Optional permanent binding: a key created with a bound agent name can
        // only ever authenticate as that agent in the /ws/agent handshake.
        if (request.BoundAgentName is not null && string.IsNullOrWhiteSpace(request.BoundAgentName))
            return BadRequest(new { error = "boundAgentName must be a non-empty string when provided." });

        var created = await _keys.CreateAsync(
            request.Name.Trim(), ApiKeyScope.Agent,
            boundAgentName: string.IsNullOrWhiteSpace(request.BoundAgentName) ? null : request.BoundAgentName.Trim(),
            ct: ct);
        return Ok(Map(created));
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var items = await _keys.ListAsync(ct);
        return Ok(items.Select(Map));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var item = await _keys.GetAsync(id, ct);
        return item is null ? NotFound(new { error = "API key not found." }) : Ok(Map(item));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Revoke(string id, CancellationToken ct)
    {
        var ok = await _keys.RevokeAsync(id, ct);
        return ok ? NoContent() : NotFound(new { error = "API key not found." });
    }

    [HttpPost("{id}/rotate")]
    public async Task<IActionResult> Rotate(string id, CancellationToken ct)
    {
        try
        {
            var rotated = await _keys.RotateAsync(id, ct);
            return Ok(Map(rotated));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "API key not found." });
        }
    }

    private static ApiKeyCreateResponse Map(CreateApiKeyResponse r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        KeyPrefix = r.KeyPrefix,
        Scope = r.Scope,
        IsActive = r.IsActive,
        BoundAgentName = r.BoundAgentName,
        CreatedAt = r.CreatedAt,
        LastUsedAt = r.LastUsedAt,
        Secret = r.Secret,
    };

    private static ApiKeyListItem Map(ApiKeyItem item) => new()
    {
        Id = item.Id,
        Name = item.Name,
        KeyPrefix = item.KeyPrefix,
        Scope = item.Scope,
        IsActive = item.IsActive,
        BoundAgentName = item.BoundAgentName,
        CreatedAt = item.CreatedAt,
        LastUsedAt = item.LastUsedAt,
    };
}
