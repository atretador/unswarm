using System.Security.Claims;

namespace Unswarm.Api.Extensions;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Resolves the acting user's id from the <see cref="ClaimTypes.NameIdentifier"/>
    /// claim. Returns <c>false</c> for API-key identities, which carry no
    /// NameIdentifier, so callers can fail closed rather than treating a missing
    /// id as "not the current user" (the H2 self-check bypass).
    /// </summary>
    public static bool TryGetActingUserId(this ClaimsPrincipal principal, out string userId)
    {
        userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
        return userId.Length > 0;
    }
}
