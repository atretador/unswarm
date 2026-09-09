using Unswarm.Core.Models;

namespace Unswarm.Core.Contracts;

public interface IRouterProfileService
{
    /// <summary>
    /// Resolve a router profile name to its enabled entries sorted by priority (ascending).
    /// Returns null if the profile doesn't exist.
    /// </summary>
    Task<IReadOnlyList<RouterProfileEntry>?> ResolveEntriesAsync(string profileName, CancellationToken ct = default);

    /// <summary>
    /// Get the profile mode (Auto vs Manual) for a given profile name.
    /// Returns null if the profile doesn't exist.
    /// </summary>
    Task<RouterProfileMode?> GetModeAsync(string profileName, CancellationToken ct = default);

    /// <summary>
    /// Resolve a router profile name to enabled entries + mode in a single DB call.
    /// Returns null if the profile doesn't exist.
    /// </summary>
    Task<(IReadOnlyList<RouterProfileEntry> Entries, RouterProfileMode Mode)?> ResolveAsync(string profileName, CancellationToken ct = default);

    /// <summary>
    /// List all router profiles.
    /// </summary>
    Task<IReadOnlyList<RouterProfile>> ListProfilesAsync(CancellationToken ct = default);

    /// <summary>
    /// Update the active model for a router profile. Pass null to clear.
    /// Used by the router profile handler when auto-fallback advances to a new model.
    /// </summary>
    Task SetActiveModelIdAsync(string profileName, string? activeModelId, CancellationToken ct = default);
}
