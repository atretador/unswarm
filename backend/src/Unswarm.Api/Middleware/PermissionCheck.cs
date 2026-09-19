using System.Text.Json;

namespace Unswarm.Api.Middleware;

/// <summary>Fine-grained permission check for ControlPlane API keys.</summary>
public static class PermissionCheck
{
    /// <summary>Read access may be satisfied by "r" or "rw".</summary>
    public static bool Read(HttpRequest request, string domain) => HasAccess(request, domain, "r");

    /// <summary>Write access requires "rw".</summary>
    public static bool Write(HttpRequest request, string domain) => HasAccess(request, domain, "rw");

    private static bool HasAccess(HttpRequest request, string domain, string requiredLevel)
    {
        var claim = request.HttpContext.User.FindFirst("unswarm:permissions");
        // A cookie administrator has no permissions claim. API keys without a
        // permissions claim (for example inference/agent keys) must not inherit
        // cookie-admin access merely because they are present on the request.
        if (claim is null) return request.HttpContext.User.IsInRole("Admin");

        var permissions = ParsePermissions(claim.Value);
        if (!permissions.TryGetValue(domain, out var level)) return false;
        return LevelRank(level) >= LevelRank(requiredLevel);
    }

    /// <summary>
    /// Parses a ControlPlane permissions JSON map. Domain keys are case-normalized
    /// (lowercased) so <c>{"APIKeys":"rw"}</c> behaves like <c>{"apikeys":"rw"}</c>.
    /// Malformed JSON yields an empty map (fail-closed).
    /// </summary>
    public static Dictionary<string, string> ParsePermissions(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed is null) return result;

            foreach (var kv in parsed)
            {
                if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                result[kv.Key.Trim().ToLowerInvariant()] = kv.Value;
            }
        }
        catch (JsonException)
        {
            // Fail-closed: an unparseable claim grants nothing.
        }

        return result;
    }

    /// <summary>Permission level ranking: none=0, r=1, rw=2.</summary>
    public static int LevelRank(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "rw" => 2,
        "r" => 1,
        _ => 0,
    };

    /// <summary>
    /// True when every permission in <paramref name="requested"/> is covered by
    /// <paramref name="held"/> (same domain at an equal or higher level). Domain
    /// keys are compared case-insensitively; missing domains rank as "none".
    /// </summary>
    public static bool IsSubsetOf(
        IReadOnlyDictionary<string, string> requested,
        IReadOnlyDictionary<string, string> held)
    {
        var heldRanks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in held)
            heldRanks[kv.Key.Trim().ToLowerInvariant()] = LevelRank(kv.Value);

        foreach (var kv in requested)
        {
            var key = kv.Key.Trim().ToLowerInvariant();
            var heldRank = heldRanks.TryGetValue(key, out var rank) ? rank : 0;
            if (heldRank < LevelRank(kv.Value)) return false;
        }

        return true;
    }
}
