using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Unswarm.Api.Configuration;
using Unswarm.Core.Contracts;
using Unswarm.Core.Helpers;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;

namespace Unswarm.Api.Middleware;

/// <summary>
/// API-key authentication for the protected surfaces, scoped by path.
///
///   /v1                       → Inference key
///   /api/agents, /ws/agent    → Agent key
///   /api (except /api/agents) → ControlPlane key
///
/// Three design rules keep the authentication surfaces strictly separate:
///  1. A key carries a <see cref="ApiKeyScope"/> claim set by ApiKeyAuthMiddleware.
///  2. Each protected path prefix maps to exactly one scope. After key validation
///     the key's scope is compared to the path's required scope; a mismatch is
///     rejected with 401.
///  3. Protected paths are FAIL-CLOSED: they accept only (a) a valid API key of
///     the required scope, or (b) an already-cookie-authenticated principal
///     (dashboard admin) so bootstrap key creation still works. When no keys
///     exist yet and the caller is anonymous, the request is rejected with 401
///     and a JSON body explaining how to bootstrap (create admin + API key).
/// </summary>
public sealed class ApiKeyAuthMiddleware
{
    /// <summary>Claim type holding the key's scope ("Inference").</summary>
    public const string ScopeClaimType = "unswarm:key-scope";

    /// <summary>
    /// Claim type holding the key's permanently bound agent name, when the key
    /// has one. Consumed by AgentController to enforce per-agent key bindings.
    /// </summary>
    public const string BoundAgentClaimType = "unswarm:key-bound-agent";

    /// <summary>Required scope per protected path prefix (case-insensitive).</summary>
    private static readonly Dictionary<string, ApiKeyScope> PathScope = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/api/agents"] = ApiKeyScope.Agent,
        ["/ws/agent"] = ApiKeyScope.Agent,
        ["/v1"] = ApiKeyScope.Inference,
        ["/api"] = ApiKeyScope.ControlPlane,
    };

    /// <summary>
    /// Minimum interval between <c>LastUsedAt</c> writes for a given key. The write
    /// is a per-request UPDATE on the hot inference path; throttling to one write
    /// per key per interval removes that write amplification while keeping the
    /// dashboard's "last used" accurate to within the window.
    /// </summary>
    private static readonly TimeSpan LastUsedWriteInterval = TimeSpan.FromSeconds(60);

    /// <summary>Last time a LastUsedAt write was issued per key id (write throttle).</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastUsedWriteAt = new(StringComparer.Ordinal);

    private readonly RequestDelegate _next;
    private readonly IApiKeyStore _store;
    private readonly IOptions<AuthOptions> _options;
    private readonly ILogger<ApiKeyAuthMiddleware> _logger;

    public ApiKeyAuthMiddleware(RequestDelegate next, IApiKeyStore store, IOptions<AuthOptions> options, ILogger<ApiKeyAuthMiddleware> logger)
    {
        _next = next;
        _store = store;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Cookie-authenticated principals (dashboard admins) bypass API-key
        // authentication entirely. They are governed by [Authorize] policies
        // on the controllers. This is intentional: the dashboard must work
        // before any API keys are created (bootstrap scenario), and cookie
        // auth carries its own role-based authorization (Admin role). Note:
        // a valid admin cookie + invalid API key on the same request still
        // succeeds — this is acceptable because the cookie identity carries
        // full admin privileges already.
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await _next(context);
            return;
        }

        string path = context.Request.Path.Value ?? "";

        // Strict paths (/v1, /api/agents, /ws/agent) require a matching API key.
        // For /api/agents specifically, fall through to ControlPlane opt-in if
        // the presented key is ControlPlane scope (allows CLI access).
        if (TryResolveScope(path, out ApiKeyScope scope))
        {
            string? presented = ReadPresentedKey(context.Request);
            if (!string.IsNullOrEmpty(presented))
            {
                var entity = await _store.AuthenticateAsync(presented, context.RequestAborted);
                if (entity is not null && entity.Scope == scope)
                {
                    // Exact scope match → strict auth
                    await ThrottledLastUsedWrite(entity);
                    context.User = WithKeyIdentity(context.User, entity);
                    await _next(context);
                    return;
                }
                // Scope mismatch on /api/agents → try ControlPlane fallback
                if (scope == ApiKeyScope.Agent && entity is not null && entity.Scope == ApiKeyScope.ControlPlane)
                {
                    await AuthenticateControlPlaneKey(context, path, presented);
                    return;
                }
                // Other scope mismatch → deny
                await DenyAsync(context, hasAnyKeys: true);
                return;
            }
            // No key presented on strict path → deny
            await DenyAsync(context, await _store.HasAnyAsync(scope, context.RequestAborted));
            return;
        }

        // /api/* paths (except strict paths handled above): if a key IS
        // presented, validate it as ControlPlane; if not, pass through and let
        // the controller's [Authorize] handle cookie-based auth.
        if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            string? presented = ReadPresentedKey(context.Request);
            if (!string.IsNullOrEmpty(presented))
            {
                await AuthenticateControlPlaneKey(context, path, presented);
                return;
            }
            // No key → pass through to controller [Authorize].
            await _next(context);
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// Strict key authentication: the path REQUIRES a key of the matching scope.
    /// Fail-closed — anonymous access is rejected.
    /// </summary>
    private async Task AuthenticateRequiredKey(HttpContext context, string path, ApiKeyScope scope)
    {
        string? presented = ReadPresentedKey(context.Request);
        if (string.IsNullOrEmpty(presented))
        {
            await DenyAsync(context, await _store.HasAnyAsync(scope, context.RequestAborted));
            return;
        }

        var entity = await _store.AuthenticateAsync(presented, context.RequestAborted);
        if (entity is null)
        {
            _logger.LogWarning("Invalid API key for {Path} from {Ip}", path, context.Connection.RemoteIpAddress);
            await DenyAsync(context, hasAnyKeys: true);
            return;
        }

        if (entity.Scope != scope)
        {
            _logger.LogWarning("API key scope {KeyScope} does not match required scope {RequiredScope} for {Path} from {Ip}", entity.Scope, scope, path, context.Connection.RemoteIpAddress);
            await DenyAsync(context, hasAnyKeys: true);
            return;
        }

        await ThrottledLastUsedWrite(entity);
        context.User = WithKeyIdentity(context.User, entity);
        await _next(context);
    }

    /// <summary>
    /// Opt-in ControlPlane key authentication: a key presented on /api/* is
    /// validated as ControlPlane scope. If the key is invalid or wrong scope,
    /// reject with 401 — do not silently pass through to cookie auth.
    /// </summary>
    private async Task AuthenticateControlPlaneKey(HttpContext context, string path, string presented)
    {
        var entity = await _store.AuthenticateAsync(presented, context.RequestAborted);
        if (entity is null)
        {
            _logger.LogWarning("Invalid API key for {Path} from {Ip}", path, context.Connection.RemoteIpAddress);
            await DenyAsync(context, hasAnyKeys: true);
            return;
        }

        if (entity.Scope != ApiKeyScope.ControlPlane)
        {
            _logger.LogWarning("API key scope {KeyScope} is not ControlPlane for {Path} from {Ip}", entity.Scope, path, context.Connection.RemoteIpAddress);
            await DenyAsync(context, hasAnyKeys: true);
            return;
        }

        await ThrottledLastUsedWrite(entity);
        context.User = WithKeyIdentity(context.User, entity);
        await _next(context);
    }

    private async Task ThrottledLastUsedWrite(Core.Persistence.ApiKeyEntity entity)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        if (!_lastUsedWriteAt.TryGetValue(entity.Id, out var lastWrite)
            || nowUtc - lastWrite >= LastUsedWriteInterval)
        {
            _lastUsedWriteAt[entity.Id] = nowUtc;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _store.UpdateLastUsedAsync(entity.Id).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to update LastUsedAt for key {KeyId}", entity.Id);
                }
            });
        }
    }

    private bool TryResolveScope(string path, out ApiKeyScope scope)
    {
        // Use the hardcoded PathScope map directly — strict paths (/v1, /api/agents,
        // /ws/agent) are invariant and must not depend on configuration.
        foreach (var (prefix, mappedScope) in PathScope.OrderByDescending(e => e.Key.Length))
        {
            // /api is the ControlPlane opt-in branch — handled in InvokeAsync, not here.
            if (prefix.Equals("/api", StringComparison.OrdinalIgnoreCase))
                continue;

            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix + "?", StringComparison.OrdinalIgnoreCase))
            {
                scope = mappedScope;
                return true;
            }
        }

        scope = default;
        return false;
    }

    private static string? ReadPresentedKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-Api-Key", out var xApiKey) && !string.IsNullOrWhiteSpace(xApiKey))
            return xApiKey.ToString().Trim();

        if (request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            string authStr = authHeader.ToString();
            if (authStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return authStr["Bearer ".Length..].Trim();
        }

        return null;
    }

    /// <summary>
    /// Combine the incoming cookie identity (if any) with a key identity carrying
    /// the scope claim, so both <c>[Authorize(Policy = "Cookie")]</c> and
    /// <c>[Authorize(Policy = "InferenceKey")]</c> can be satisfied from one request.
    /// </summary>
    private static ClaimsPrincipal WithKeyIdentity(ClaimsPrincipal existing, ApiKeyEntity entity)
    {
        var identities = new List<ClaimsIdentity>();
        if (existing.Identity is ClaimsIdentity ci)
            identities.Add(ci);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, entity.Name),
            new("unswarm:key-id", entity.Id),
            new(ScopeClaimType, entity.Scope.ToString()),
        };
        if (entity.BoundAgentName is not null)
            claims.Add(new Claim(BoundAgentClaimType, entity.BoundAgentName));

        if (entity.Scope == ApiKeyScope.ControlPlane)
        {
            if (!string.IsNullOrWhiteSpace(entity.PermissionsJson) && entity.PermissionsJson != "{}")
                claims.Add(new Claim("unswarm:permissions", entity.PermissionsJson));
        }

        identities.Add(new ClaimsIdentity(claims, authenticationType: "ApiKey"));

        return new ClaimsPrincipal(identities);
    }

    /// <summary>
    /// Reject the request with 401. When the key store has no keys at all, the
    /// body explains the bootstrap path (create an admin user and generate an
    /// API key) instead of a generic unauthorized error.
    /// </summary>
    private static async Task DenyAsync(HttpContext context, bool hasAnyKeys)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";

        object payload = hasAnyKeys
            ? LocalizedError.Create("auth.unauthorized")
            : LocalizedError.Create("auth.noKeysBootstrap");

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}
