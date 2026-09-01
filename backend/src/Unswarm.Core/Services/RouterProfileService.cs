using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Core.Services;

public sealed class RouterProfileService : IRouterProfileService
{
    private readonly IRouterProfileStore _store;

    public RouterProfileService(IRouterProfileStore store)
    {
        _store = store;
    }

    public async Task<IReadOnlyList<RouterProfileEntry>?> ResolveEntriesAsync(string profileName, CancellationToken ct = default)
    {
        var profile = await _store.GetByNameAsync(profileName, ct);
        if (profile is null)
            return null;

        return profile.Entries
            .Where(e => e.IsEnabled)
            .OrderBy(e => e.Priority)
            .ToList();
    }

    public async Task<RouterProfileMode?> GetModeAsync(string profileName, CancellationToken ct = default)
    {
        var profile = await _store.GetByNameAsync(profileName, ct);
        return profile?.Mode;
    }

    public async Task<(IReadOnlyList<RouterProfileEntry> Entries, RouterProfileMode Mode)?> ResolveAsync(string profileName, CancellationToken ct = default)
    {
        var profile = await _store.GetByNameAsync(profileName, ct);
        if (profile is null)
            return null;

        var enabledEntries = profile.Entries
            .Where(e => e.IsEnabled)
            .OrderBy(e => e.Priority)
            .ToList();

        // If ActiveModelId is set, move that entry to the front
        if (!string.IsNullOrEmpty(profile.ActiveModelId))
        {
            var activeEntry = enabledEntries.FirstOrDefault(e => e.ModelId == profile.ActiveModelId);
            if (activeEntry is not null)
            {
                enabledEntries.Remove(activeEntry);
                enabledEntries.Insert(0, activeEntry);
            }
        }

        return (enabledEntries, profile.Mode);
    }

    public async Task<IReadOnlyList<RouterProfile>> ListProfilesAsync(CancellationToken ct = default)
    {
        return await _store.ListAsync(ct);
    }
}
