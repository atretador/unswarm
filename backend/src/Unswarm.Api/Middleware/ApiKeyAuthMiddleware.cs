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
///  3. Auth is opt-in per scope: a protected path only enforces a key once at
///     least one active key of that scope exists. This preserves the historical
///     "empty key = auth disabled" behaviour for environments that run without
///     keys, while newly created keys flip the relevant surface on.
/// </summary>
public sealed class ApiKeyAuthMiddleware
{
    /// <summary>Claim type holding the key's scope ("Inference").</summary>
    public const string ScopeClaimType = "unswarm:key-scope";

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

        // Opt-in: enforce a key on this surface as soon as any key of the scope
        // exists (active or retired). Before that, the surface stays open —
        // backward compatible with no-key setups.
        if (!await _store.HasAnyAsync(scope, context.RequestAborted))
        {
            await _next(context);
            return;
        }

        string? presented = ReadPresentedKey(context.Request);
        if (string.IsNullOrEmpty(presented))
        {
            await DenyAsync(context);
            return;
        }

        var entity = await _store.AuthenticateAsync(presented, context.RequestAborted);
        if (entity is null)
        {
            _logger.LogWarning("Invalid API key for {Path} from {Ip}", path, context.Connection.RemoteIpAddress);
            await DenyAsync(context);
            return;
        }

        if (entity.Scope != scope)
        {
            _logger.LogWarning("API key scope {KeyScope} does not match required scope {RequiredScope} for {Path} from {Ip}", entity.Scope, scope, path, context.Connection.RemoteIpAddress);
            await DenyAsync(context);
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

        identities.Add(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Name, entity.Name),
                new Claim("unswarm:key-id", entity.Id),
                new Claim(ScopeClaimType, entity.Scope.ToString()),
            },
            authenticationType: "ApiKey"));

        return new ClaimsPrincipal(identities);
    }

    private static async Task DenyAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Unauthorized" }));
    }
}