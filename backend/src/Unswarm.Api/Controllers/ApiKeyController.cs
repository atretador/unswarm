using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text.Json;
using Unswarm.Api.Dtos;
using Unswarm.Api.Middleware;
using Unswarm.Core.Contracts;
using Unswarm.Core.Helpers;
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
[Authorize(Policy = "ControlPlaneAccess")]
public sealed class ApiKeyController : ControllerBase
{
    private readonly IApiKeyStore _keys;
    private readonly ICloudProviderStore _cloudProviders;
    private readonly IContainerRegistry _containers;
    private readonly IRouterProfileStore _routerProfiles;
    private readonly ILogger<ApiKeyController> _logger;

    public ApiKeyController(IApiKeyStore keys, ICloudProviderStore cloudProviders, IContainerRegistry containers, IRouterProfileStore routerProfiles, ILogger<ApiKeyController> logger)
    {
        _keys = keys;
        _cloudProviders = cloudProviders;
        _containers = containers;
        _routerProfiles = routerProfiles;
        _logger = logger;
    }

    /// <summary>
    /// Canonical set of permission domains accepted by ControlPlane keys.
    /// Must match the frontend CLI_DOMAINS array. A key with an unknown domain
    /// gains no access — but rejecting unknowns at write time prevents confusion
    /// and keeps the backend as the single source of truth.
    /// </summary>
    private static readonly HashSet<string> ValidDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "models", "runtimes", "agents", "queue", "benchmarks", "prompts",
        "settings", "users", "apikeys", "routerprofiles", "cloudproviders",
        "metrics", "logs", "scripts", "stats",
    };

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateApiKeyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(LocalizedError.Create("apiKeys.nameRequired"));

        // Inference keys are unrestricted on /v1 (empty access = unrestricted), so
        // an apikeys:rw ControlPlane key must not be able to mint one. Admin only.
        if (!RequireAdminForGrant("Create(Inference)", targetId: null, requestedDomains: []))
            return Forbid();

        // Inference-scope key creation. Agent-scoped keys are created
        // through POST api/api-keys/agent below.
        var created = await _keys.CreateAsync(request.Name.Trim(), ApiKeyScope.Inference, ct: ct);
        return Ok(Map(created));
    }

    [HttpPost("agent")]
    public async Task<IActionResult> CreateAgent([FromBody] CreateApiKeyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(LocalizedError.Create("apiKeys.nameRequired"));

        if (request.BoundAgentName is not null && string.IsNullOrWhiteSpace(request.BoundAgentName))
            return BadRequest(LocalizedError.Create("apiKeys.invalidBoundAgentName"));

        // Agent keys are an agent-surface identity; apikeys:rw must not mint one.
        if (!RequireAdminForGrant("CreateAgent", targetId: null, requestedDomains: []))
            return Forbid();

        var created = await _keys.CreateAsync(
            request.Name.Trim(), ApiKeyScope.Agent,
            boundAgentName: string.IsNullOrWhiteSpace(request.BoundAgentName) ? null : request.BoundAgentName.Trim(),
            ct: ct);
        return Ok(Map(created));
    }

    /// <remarks>
    /// SECURITY NOTE: ControlPlane permission maps are subset-enforced via
    /// <see cref="CallerMayGrant"/>: a non-Admin caller can only grant
    /// permissions it already holds, and Admin may grant anything. Creating
    /// Inference/Agent keys and rotating/revoking non-ControlPlane keys
    /// additionally requires the Admin role (see
    /// <see cref="RequireAdminForGrant"/>). A caller holding only
    /// "apikeys:rw" therefore cannot delegate permissions it does not have or
    /// mint an agent-surface identity.
    /// </remarks>
    [HttpPost("control-plane")]
    public async Task<IActionResult> CreateControlPlane([FromBody] CreateControlPlaneKeyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(LocalizedError.Create("apiKeys.nameRequired"));
        var permissions = request.Permissions ?? [];
        var validLevels = new HashSet<string> { "none", "r", "rw" };
        if (permissions.Any(p => !validLevels.Contains(p.Value)))
            return BadRequest(LocalizedError.Create("apiKeys.invalidPermissionLevel"));
        var unknownDomains = permissions.Keys.Where(k => !ValidDomains.Contains(k)).ToList();
        if (unknownDomains.Count > 0)
            return BadRequest(LocalizedError.Create("apiKeys.invalidPermissionDomain"));

        // A non-Admin caller may only grant permissions it already holds.
        if (!CallerMayGrant(User, permissions))
        {
            LogGrantDenied(targetId: null, requestedDomains: permissions.Keys);
            return Forbid();
        }

        var permissionsJson = JsonSerializer.Serialize(permissions, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var created = await _keys.CreateAsync(request.Name.Trim(), ApiKeyScope.ControlPlane, permissionsJson: permissionsJson, ct: ct);
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
        return item is null ? NotFound(LocalizedError.Create("apiKeys.notFound")) : Ok(Map(item));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Revoke(string id, CancellationToken ct)
    {
        if (User?.FindFirst("unswarm:key-id")?.Value == id)
            return Forbid();

        var item = await _keys.GetAsync(id, ct);
        if (item is null)
            return NotFound(LocalizedError.Create("apiKeys.notFound"));

        if (!await CallerMayManageTargetAsync(item, ct))
            return Forbid();

        var ok = await _keys.RevokeAsync(id, ct);
        return ok ? NoContent() : NotFound(LocalizedError.Create("apiKeys.notFound"));
    }

    [HttpPost("{id}/rotate")]
    public async Task<IActionResult> Rotate(string id, CancellationToken ct)
    {
        if (User?.FindFirst("unswarm:key-id")?.Value == id)
            return Forbid();

        var item = await _keys.GetAsync(id, ct);
        if (item is null)
            return NotFound(LocalizedError.Create("apiKeys.notFound"));

        if (!await CallerMayManageTargetAsync(item, ct))
            return Forbid();

        try
        {
            var rotated = await _keys.RotateAsync(id, ct);
            return Ok(Map(rotated));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(LocalizedError.Create("apiKeys.notFound"));
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
            return NotFound(LocalizedError.Create("apiKeys.notFound"));
        if (item.Scope == ApiKeyScope.Agent)
            return BadRequest(LocalizedError.Create("apiKeys.agentKeyNoAccess"));

        var access = await _keys.GetAccessAsync(id, ct);
        return access is null
            ? NotFound(LocalizedError.Create("apiKeys.notFound"))
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
            return NotFound(LocalizedError.Create("apiKeys.notFound"));
        if (item.Scope == ApiKeyScope.Agent)
            return BadRequest(LocalizedError.Create("apiKeys.agentKeyNoAccess"));

        var providers = (request.Providers ?? []).Select(p => p.Trim()).Where(p => p.Length > 0).Distinct().ToList();
        var models = (request.Models ?? []).Select(m => m.Trim()).Where(m => m.Length > 0).Distinct().ToList();

        if (providers.Count > 200 || models.Count > 500)
            return BadRequest(LocalizedError.Create("apiKeys.tooManyAccessEntries"));

        // Strict validation: every listed provider must be a configured cloud provider,
        // a registered local runtime display name, or a router profile name.
        var configuredProviders = (await _cloudProviders.ListAsync(ct)).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var runtimeNames = (await _containers.ListAllAsync(ct)).Select(r => r.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var routerNames = (await _routerProfiles.ListAsync(ct)).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = providers.Where(p => !configuredProviders.Contains(p) && !runtimeNames.Contains(p) && !routerNames.Contains(p)).ToList();
        if (unknown.Count > 0)
        {
            _logger.LogWarning("Stripping unknown provider(s) from API key access: {Providers}", string.Join(", ", unknown));
            providers = providers.Where(p => !unknown.Contains(p)).ToList();
        }

        var saved = await _keys.SaveAccessAsync(id, new KeyAccess { Providers = providers, Models = models }, ct);
        if (saved is null)
            return NotFound(LocalizedError.Create("apiKeys.notFound"));

        return Ok(new KeyAccessDto { Providers = [.. saved.Providers], Models = [.. saved.Models] });
    }

    [HttpGet("{id}/permissions")]
    public async Task<IActionResult> GetPermissions(string id, CancellationToken ct)
    {
        var item = await _keys.GetAsync(id, ct);
        if (item is null) return NotFound(LocalizedError.Create("apiKeys.notFound"));
        if (item.Scope != ApiKeyScope.ControlPlane) return BadRequest(LocalizedError.Create("apiKeys.notControlPlaneKey"));
        var permissions = await _keys.GetPermissionsAsync(id, ct);
        return Ok(new ApiKeyPermissionsDto { Permissions = permissions ?? [] });
    }

    [HttpPut("{id}/permissions")]
    public async Task<IActionResult> SavePermissions(string id, [FromBody] ApiKeyPermissionsDto request, CancellationToken ct)
    {
        if (User?.FindFirst("unswarm:key-id")?.Value == id)
            return Forbid();

        var item = await _keys.GetAsync(id, ct);
        if (item is null) return NotFound(LocalizedError.Create("apiKeys.notFound"));
        if (item.Scope != ApiKeyScope.ControlPlane) return BadRequest(LocalizedError.Create("apiKeys.notControlPlaneKey"));
        var validLevels = new HashSet<string> { "none", "r", "rw" };
        if (request.Permissions.Any(p => !validLevels.Contains(p.Value)))
            return BadRequest(LocalizedError.Create("apiKeys.invalidPermissionLevel"));
        var unknownDomains = request.Permissions.Keys.Where(k => !ValidDomains.Contains(k)).ToList();
        if (unknownDomains.Count > 0)
            return BadRequest(LocalizedError.Create("apiKeys.invalidPermissionDomain"));

        // A non-Admin caller may only keep/replace a ControlPlane key with a
        // permission map that it already holds (both the new and current maps).
        var targetPermissions = await _keys.GetPermissionsAsync(id, ct) ?? [];
        if (!CallerMayGrant(User, request.Permissions, targetPermissions))
        {
            LogGrantDenied(id, request.Permissions.Keys);
            return Forbid();
        }

        var json = JsonSerializer.Serialize(request.Permissions, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var saved = await _keys.SavePermissionsAsync(id, json, ct);
        return saved is null
            ? NotFound(LocalizedError.Create("apiKeys.notFound"))
            : Ok(new ApiKeyPermissionsDto { Permissions = request.Permissions });
    }

    /// <summary>
    /// True when the caller may grant <paramref name="requested"/>: Admin always,
    /// otherwise every requested permission must be a subset of the caller's own
    /// ControlPlane permissions, and (for existing targets)
    /// <paramref name="targetCurrent"/> must be a subset too so a caller cannot
    /// shed a permission it does not hold.
    /// </summary>
    public static bool CallerMayGrant(
        ClaimsPrincipal caller,
        IReadOnlyDictionary<string, string> requested,
        IReadOnlyDictionary<string, string>? targetCurrent = null)
    {
        if (caller.IsInRole("Admin")) return true;

        var held = PermissionCheck.ParsePermissions(caller.FindFirst("unswarm:permissions")?.Value);
        if (!PermissionCheck.IsSubsetOf(requested, held)) return false;
        if (targetCurrent is not null && !PermissionCheck.IsSubsetOf(targetCurrent, held)) return false;
        return true;
    }

    /// <summary>
    /// Non-ControlPlane key lifecycle (Inference/Agent create + rotate/revoke)
    /// requires an Admin caller (decision 13).
    /// </summary>
    private bool RequireAdminForGrant(string operation, string? targetId, IReadOnlyCollection<string> requestedDomains)
    {
        if (User.IsInRole("Admin")) return true;

        LogGrantDenied(targetId, requestedDomains);
        _logger.LogWarning("API key grant requires Admin for {Operation}", operation);
        return false;
    }

    private async Task<bool> CallerMayManageTargetAsync(ApiKeyItem item, CancellationToken ct)
    {
        if (item.Scope != ApiKeyScope.ControlPlane)
        {
            if (User.IsInRole("Admin")) return true;
            LogGrantDenied(item.Id, Array.Empty<string>());
            return false;
        }

        var targetPermissions = await _keys.GetPermissionsAsync(item.Id, ct) ?? [];
        if (CallerMayGrant(User, targetPermissions)) return true;
        LogGrantDenied(item.Id, targetPermissions.Keys);
        return false;
    }

    /// <summary>Log a denied grant. Never logs secrets — only ids and domain names.</summary>
    private void LogGrantDenied(string? targetId, IEnumerable<string> requestedDomains)
    {
        var callerId = User?.FindFirst("unswarm:key-id")?.Value;
        _logger.LogWarning(
            "API key permission grant denied. Caller={CallerKeyId} Target={TargetId} RequestedDomains={RequestedDomains}",
            string.IsNullOrEmpty(callerId) ? "(cookie)" : callerId,
            targetId ?? "(new)",
            string.Join(",", requestedDomains));
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
