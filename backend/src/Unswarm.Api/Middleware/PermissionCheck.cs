using System.Text.Json;

namespace Unswarm.Api.Middleware;

/// <summary>Fine-grained permission check for ControlPlane API keys.</summary>
public static class PermissionCheck
{
    public static bool Read(HttpRequest request, string domain) => HasAccess(request, domain, "r");
    public static bool Write(HttpRequest request, string domain) => HasAccess(request, domain, "rw");

    private static bool HasAccess(HttpRequest request, string domain, string requiredLevel)
    {
        var claim = request.HttpContext.User.FindFirst("unswarm:permissions");
        // A cookie administrator has no permissions claim. API keys without a
        // permissions claim (for example inference/agent keys) must not inherit
        // cookie-admin access merely because they are present on the request.
        if (claim is null) return request.HttpContext.User.IsInRole("Admin");
        try
        {
            var permissions = JsonSerializer.Deserialize<Dictionary<string, string>>(claim.Value,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            if (!permissions.TryGetValue(domain, out var level)) return false;
            return level == "rw" || (level == "r" && requiredLevel == "r");
        }
        catch (JsonException) { return false; }
    }
}
