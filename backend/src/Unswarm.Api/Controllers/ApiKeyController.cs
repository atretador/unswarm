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
/// <remarks>
/// POST /api/api-keys — Create an inference API key
/// POST /api/api-keys/agent — Create an agent API key
/// GET /api/api-keys — List API keys
/// GET /api/api-keys/{id} — Get an API key detail
/// DELETE /api/api-keys/{id} — Revoke an API key
/// POST /api/api-keys/{id}/rotate — Rotate an API key
/// GET /api/api-keys/{id}/access — Get key access grants
/// PUT /api/api-keys/{id}/access — Update key access grants
/// </remarks>
[ApiController]
// Explicit route — ASP.NET lowercases the [controller] token to "apikeys",
// which would not match the frontend wire contract at "/api/api-keys".
[Route("api/api-keys")]
[Authorize(Roles = "Admin")]
public sealed class ApiKeyController : ControllerBase
{
    private readonly IApiKeyStore _keys;
    private readonly ICloudProviderStore _cloudProviders;

    public ApiKeyController(IApiKeyStore keys, ICloudProviderStore cloudProviders)
    {
        _keys = keys;
        _cloudProviders = cloudProviders;
    }

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

    /// <summary>
    /// Parsed per-key access restrictions. Empty arrays = unrestricted.
    /// </summary>
    [HttpGet("{id}/access")]
    public async Task<IActionResult> GetAccess(string id, CancellationToken ct)
    {
        var item = await _keys.GetAsync(id, ct);
        if (item is null)
            return NotFound(new { error = "API key not found." });
        if (item.Scope == ApiKeyScope.Agent)
            return BadRequest(new { error = "Access restrictions are not supported for agent API keys." });

        var access = await _keys.GetAccessAsync(id, ct);
        return access is null
            ? NotFound(new { error = "API key not found." })
            : Ok(new KeyAccessDto { Providers = [.. access.Providers], Models = [.. access.Models] });
    }

    /// <summary>
    /// Validates and saves per-key access restrictions. Listed cloud providers
    /// must exist (400 otherwise); models are accepted leniently since both cloud
    /// model lists and local runtime mappings are dynamic.
    /// </summary>
    [HttpPut("{id}/access")]
    public async Task<IActionResult> SaveAccess(string id, [FromBody] KeyAccessDto request, CancellationToken ct)
    {
        // Access grants are only enforced for inference-scope keys on /v1;
        // agent-scope keys must not carry them.
        var item = await _keys.GetAsync(id, ct);
        if (item is null)
            return NotFound(new { error = "API key not found." });
        if (item.Scope == ApiKeyScope.Agent)
            return BadRequest(new { error = "Access restrictions are not supported for agent API keys." });

        var providers = (request.Providers ?? []).Select(p => p.Trim()).Where(p => p.Length > 0).Distinct().ToList();
        var models = (request.Models ?? []).Select(m => m.Trim()).Where(m => m.Length > 0).Distinct().ToList();

        if (providers.Count > 200 || models.Count > 500)
            return BadRequest(new { error = "Too many entries (max 200 providers, 500 models)." });

        // Strict validation: every listed provider must be a configured cloud provider.
        var configuredProviders = (await _cloudProviders.ListAsync(ct)).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = providers.Where(p => !configuredProviders.Contains(p)).ToList();
        if (unknown.Count > 0)
            return BadRequest(new { error = $"Unknown provider(s): {string.Join(", ", unknown)}" });

        var saved = await _keys.SaveAccessAsync(id, new KeyAccess { Providers = providers, Models = models }, ct);
        if (saved is null)
            return NotFound(new { error = "API key not found." });

        return Ok(new KeyAccessDto { Providers = [.. saved.Providers], Models = [.. saved.Models] });
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
