using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Unswarm.Api.Middleware;

/// <summary>Applies the permission matrix to ControlPlane API-key requests.</summary>
public sealed class ControlPlanePermissionFilter : IAsyncActionFilter
{
    private static readonly (string Prefix, string Domain)[] DomainMap =
    [
        ("/api/api-keys", "apikeys"),
        ("/api/users", "users"),
        ("/api/cloudproviders", "cloudproviders"),
        ("/api/provider-model-catalog", "routerprofiles"),
        ("/api/router-profiles", "routerprofiles"),
        ("/api/logs", "logs"),
        ("/api/stats", "stats"),
        ("/api/settings", "settings"),
        ("/api/queue", "queue"),
        ("/api/metrics", "metrics"),
        ("/api/models", "models"),
        ("/api/containers", "runtimes"),
        ("/api/agents", "agents"),
        ("/api/benchmarks", "benchmarks"),
        ("/api/prompts", "prompts"),
        ("/api/scripts", "scripts"),
    ];

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;
        var isControlPlaneKey = user.FindFirst(ApiKeyAuthMiddleware.ScopeClaimType)?.Value
            .Equals("ControlPlane", StringComparison.OrdinalIgnoreCase) == true;

        if (!isControlPlaneKey || user.IsInRole("Admin"))
        {
            await next();
            return;
        }

        var path = context.HttpContext.Request.Path.Value ?? string.Empty;
        var match = DomainMap
            .OrderByDescending(entry => entry.Prefix.Length)
            .FirstOrDefault(entry => IsPath(entry.Prefix, path));
        if (match.Domain is null)
        {
            context.Result = new ForbidResult();
            return;
        }

        var isRead = HttpMethods.IsGet(context.HttpContext.Request.Method)
            || HttpMethods.IsHead(context.HttpContext.Request.Method);
        var allowed = isRead
            ? PermissionCheck.Read(context.HttpContext.Request, match.Domain)
            : PermissionCheck.Write(context.HttpContext.Request, match.Domain);
        if (!allowed)
        {
            context.Result = new ForbidResult();
            return;
        }

        await next();
    }

    private static bool IsPath(string prefix, string path) =>
        path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(prefix + "?", StringComparison.OrdinalIgnoreCase);
}
