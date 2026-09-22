using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Scoped authorization convention check for the management API.
///
/// Every <c>/api/*</c> action must carry an explicit authorization policy
/// (<c>[Authorize(Policy = "...")]</c>), an <c>[AllowAnonymous]</c>, or appear in
/// the checked-in baseline of known bare-<c>[Authorize]</c> surfaces below. The
/// baseline intentionally captures the read-only surfaces whose policy is applied
/// by <c>ApiKeyAuthMiddleware</c> / <c>ControlPlanePermissionFilter</c> today, so
/// the test is green on arrival and fails only when a NEW bare action is added.
///
/// This test does not smuggle M2 (bare read exposure) into scope: bare actions
/// that already existed at the time this baseline was checked in are allowed,
/// they are just frozen.
/// </summary>
public sealed class AuthorizationConventionTests
{
    /// <summary>
    /// Known bare <c>[Authorize]</c> (or un-attributed) action surfaces that are
    /// deliberately allowed without an explicit named policy. Key format is
    /// <c>{ControllerTypeName}.{ActionMethodName}</c>. Additions to this list
    /// require a security review; the test fails on any new bare action.
    /// </summary>
    private static readonly HashSet<string> BaselineBareActions = new(StringComparer.Ordinal)
    {
        // AgentsController — /api/agents is intentionally reachable by Agent keys.
        "AgentsController.List",
        "AgentsController.ListAgentScripts",
        "AgentsController.ListAvailableScripts",

        // BenchmarksController — List only (Run is ControlPlaneAccess).
        "BenchmarksController.List",

        // ContainersController — read-only surfaces.
        "ContainersController.List",
        "ContainersController.ListRegistered",
        "ContainersController.GetRegistered",

        // MetricsController — read-only surfaces (PurgeUsage is ControlPlaneAccess;
        // LiveTail is on the absolute /ws/metrics route, outside /api/*).
        "MetricsController.GetUsage",
        "MetricsController.GetSummary",
        "MetricsController.GetModels",
        "MetricsController.GetProviders",
        "MetricsController.GetProviderCatalog",
        "MetricsController.GetApiKeyUsage",
        "MetricsController.GetApiKeyUsageDetail",
        "MetricsController.GetLatencyBands",
        "MetricsController.GetTotals",

        // ModelsController — read-only surfaces.
        "ModelsController.List",
        "ModelsController.Get",

        // PromptsController — read-only surfaces.
        "PromptsController.List",
        "PromptsController.Get",
        "PromptsController.ListVersions",
        "PromptsController.GetVersion",

        // QueueController — snapshot read.
        "QueueController.GetSnapshot",

        // RuntimeStatusController — stream.
        "RuntimeStatusController.Stream",

        // SettingsController — read.
        "SettingsController.Get",

        // StatsController — read.
        "StatsController.Get",

        // AuthController — session-scoped actions (Login is AllowAnonymous).
        "AuthController.Logout",
        "AuthController.Me",
        "AuthController.ChangePassword",
        "AuthController.UpdateLocalePreference",
        "AuthController.GetLocalePreference",
    };

    [Fact]
    public void EveryManagementApiAction_HasExplicitPolicy_OrIsBaselinedBare()
    {
        var violations = new List<string>();

        foreach (var (type, method, actionKey) in EnumerateManagementActions())
        {
            if (HasAllowAnonymous(type, method)) continue;
            if (HasPolicy(type, method)) continue;

            // No explicit policy → bare surface. Frozen baseline only.
            if (!BaselineBareActions.Contains(actionKey))
                violations.Add(actionKey);
        }

        Assert.True(violations.Count == 0,
            "New /api/* action(s) without an explicit authorization policy: " +
            string.Join(", ", violations.OrderBy(v => v)) +
            ". Add [Authorize(Policy = \"...\")] or, with security review, add to " +
            nameof(BaselineBareActions) + ".");
    }

    [Fact]
    public void BaselineEntries_StillExist()
    {
        // Guards against stale baseline entries masking a removed action.
        var seen = EnumerateManagementActions().Select(a => a.ActionKey).ToHashSet(StringComparer.Ordinal);
        var stale = BaselineBareActions.Where(b => !seen.Contains(b)).ToList();
        Assert.True(stale.Count == 0,
            "Baseline references actions that no longer exist: " + string.Join(", ", stale));
    }

    private static IEnumerable<(Type Type, MethodInfo Method, string ActionKey)> EnumerateManagementActions()
    {
        var assembly = typeof(Unswarm.Api.Controllers.AuthController).Assembly;

        foreach (var type in assembly.GetTypes()
                     .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t)))
        {
            var controllerRoute = type.GetCustomAttribute<RouteAttribute>()?.Template;

            foreach (var method in type.GetMethods(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var http = method.GetCustomAttributes<HttpMethodAttribute>(inherit: true).FirstOrDefault();
                if (http is null) continue;

                var methodTemplate = http.Template;
                string? effective;
                if (methodTemplate is not null && methodTemplate.StartsWith('/'))
                {
                    // Absolute method route (e.g. /ws/metrics, /ws/agent).
                    effective = methodTemplate.TrimStart('/');
                }
                else if (controllerRoute is not null)
                {
                    effective = string.IsNullOrEmpty(methodTemplate)
                        ? controllerRoute.Trim('/')
                        : controllerRoute.Trim('/') + "/" + methodTemplate.Trim('/');
                }
                else
                {
                    continue;
                }

                if (!IsManagementApiRoute(effective)) continue;

                yield return (type, method, $"{type.Name}.{method.Name}");
            }
        }
    }

    private static bool IsManagementApiRoute(string route) =>
        route.Equals("api", StringComparison.OrdinalIgnoreCase)
        || route.StartsWith("api/", StringComparison.OrdinalIgnoreCase);

    private static bool HasAllowAnonymous(Type type, MethodInfo method) =>
        method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any()
        || type.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any();

    private static bool HasPolicy(Type type, MethodInfo method)
    {
        return method.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                   .Concat(type.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
                   .Any(a => !string.IsNullOrEmpty(a.Policy));
    }
}
