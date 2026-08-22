using Unswarm.Core.Models;

namespace Unswarm.Core.Services;

/// <summary>
/// Single source of truth for co-location compatibility between registered runtimes,
/// used by both the scheduler (<c>StopIncompatibleContainersAsync</c>) and the
/// non-scheduler start path (<c>ContainerRegistrationService.EnforceCoexistenceAsync</c>).
/// Compatibility is SYMMETRIC: two containers may run together only when each one's
/// <see cref="RegisteredRuntime.CanRunAlongWith"/> list names the other (by image or
/// display name, case-insensitive). An empty list on either side means that container
/// runs alone — nothing may co-locate with it.
/// </summary>
public static class CoexistencePolicy
{
    /// <summary>
    /// Symmetric allow-list check. Returns true only when <paramref name="a"/> allows
    /// <paramref name="b"/> AND <paramref name="b"/> allows <paramref name="a"/>.
    /// Empty allow list on either side → not allowed to coexist (runs alone).
    /// </summary>
    public static bool IsAllowedToCoexist(RegisteredRuntime a, RegisteredRuntime b)
    {
        if (a.CanRunAlongWith.Count == 0 || b.CanRunAlongWith.Count == 0)
            return false;

        return AllowListContains(a.CanRunAlongWith, b) && AllowListContains(b.CanRunAlongWith, a);
    }

    private static bool AllowListContains(IReadOnlyList<string> allowList, RegisteredRuntime other) =>
        allowList.Any(name =>
            string.Equals(name, other.Image, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(other.DisplayName) &&
             string.Equals(name, other.DisplayName, StringComparison.OrdinalIgnoreCase)));
}
