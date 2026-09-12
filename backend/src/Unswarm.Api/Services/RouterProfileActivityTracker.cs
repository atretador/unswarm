using System.Collections.Concurrent;

namespace Unswarm.Api.Services;

/// <summary>
/// Tracks the number of active in-flight requests per router profile name.
/// Used by the status endpoint for the frontend's activity indicator.
/// </summary>
public sealed class RouterProfileActivityTracker
{
    private readonly ConcurrentDictionary<string, int> _activeByProfile = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Increment the active count for a profile.
    /// </summary>
    public void Increment(string profileName) =>
        _activeByProfile.AddOrUpdate(profileName, 1, (_, c) => c + 1);

    /// <summary>
    /// Decrement the active count for a profile. Removes the key when it reaches zero.
    /// </summary>
    public void Decrement(string profileName)
    {
        while (_activeByProfile.TryGetValue(profileName, out var current))
        {
            var next = current - 1;
            if (next <= 0)
            {
                if (_activeByProfile.TryRemove(profileName, out _))
                    return;
            }
            else
            {
                if (_activeByProfile.TryUpdate(profileName, next, current))
                    return;
            }
        }
    }

    /// <summary>
    /// Snapshot of profile name → active request count.
    /// Only includes profiles with count > 0.
    /// </summary>
    public IReadOnlyDictionary<string, int> GetActiveByProfile() =>
        _activeByProfile
            .Where(kv => kv.Value > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
}
