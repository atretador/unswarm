using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Unswarm.Api.Configuration;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;

namespace Unswarm.Api.Middleware;

/// <summary>
/// API-key authentication for the protected surfaces, scoped by path.
///
///   /v1                       → Inference key
///   /api/agents, /ws/agent    → Agent key
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
    };

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
        // Control-plane (cookie) principals are governed by [Authorize] policies
        // on the controllers, not here. Let them through to the authorization layer.
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await _next(context);
            return;
        }

        string path = context.Request.Path.Value ?? "";

        if (!TryResolveScope(path, out ApiKeyScope scope))
        {
            await _next(context);
            return;
        }

        // Fail-closed: a protected path requires a valid API key of the required
        // scope. There is no opt-in bypass — an empty key store must never leave
        // the surface anonymous-accessible.
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

        await _store.UpdateLastUsedAsync(entity.Id, context.RequestAborted);
        context.User = WithKeyIdentity(context.User, entity);

        await _next(context);
    }

    private bool TryResolveScope(string path, out ApiKeyScope scope)
    {
        foreach (var prefix in _options.Value.ProtectedPaths)
        {
            if (string.IsNullOrEmpty(prefix))
                continue;

            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix + "?", StringComparison.OrdinalIgnoreCase))
            {
                scope = PathScope.TryGetValue(prefix, out var s) ? s : ApiKeyScope.Inference;
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
            ? new { error = "Unauthorized" }
            : new
            {
                error = "Unauthorized: no API keys exist yet. Bootstrap the server by creating an admin user (unswarm --admin-setup <password> or UNSWARM_ADMIN_PASSWORD), sign in to the dashboard, and generate an API key."
            };

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}