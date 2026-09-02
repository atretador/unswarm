using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services;

namespace Unswarm.Tests.Unit;

public sealed class RouterProfileServiceTests
{
    private static IRouterProfileStore NewStore() => TestRouterProfileStore.Create();
    private static IRouterProfileService NewService() => new RouterProfileService(NewStore());

    [Fact]
    public async Task ResolveEntriesAsync_ReturnsEnabledEntriesSortedByPriority()
    {
        var store = NewStore();
        await store.CreateAsync(new RouterProfile
        {
            Id = "",
            Name = "sorted-profile",
            Mode = RouterProfileMode.Auto,
            Entries =
            [
                new RouterProfileEntry { ModelId = "model-c", Priority = 3, IsEnabled = true },
                new RouterProfileEntry { ModelId = "model-a", Priority = 1, IsEnabled = true },
                new RouterProfileEntry { ModelId = "model-b", Priority = 2, IsEnabled = true },
            ],
            CreatedAt = default,
            UpdatedAt = default,
        });

        var service = new RouterProfileService(store);
        var entries = await service.ResolveEntriesAsync("sorted-profile");

        Assert.NotNull(entries);
        Assert.Equal(3, entries!.Count);
        Assert.Equal("model-a", entries[0].ModelId);
        Assert.Equal("model-b", entries[1].ModelId);
        Assert.Equal("model-c", entries[2].ModelId);
    }

    [Fact]
    public async Task ResolveEntriesAsync_FiltersDisabledEntries()
    {
        var store = NewStore();
        await store.CreateAsync(new RouterProfile
        {
            Id = "",
            Name = "filter-profile",
            Mode = RouterProfileMode.Auto,
            Entries =
            [
                new RouterProfileEntry { ModelId = "enabled-1", Priority = 0, IsEnabled = true },
                new RouterProfileEntry { ModelId = "disabled-1", Priority = 1, IsEnabled = false },
                new RouterProfileEntry { ModelId = "enabled-2", Priority = 2, IsEnabled = true },
            ],
            CreatedAt = default,
            UpdatedAt = default,
        });

        var service = new RouterProfileService(store);
        var entries = await service.ResolveEntriesAsync("filter-profile");

        Assert.NotNull(entries);
        Assert.Equal(2, entries!.Count);
        Assert.Equal("enabled-1", entries[0].ModelId);
        Assert.Equal("enabled-2", entries[1].ModelId);
    }

    [Fact]
    public async Task ResolveEntriesAsync_ReturnsNullForUnknownProfile()
    {
        var service = NewService();
        var entries = await service.ResolveEntriesAsync("nonexistent");

        Assert.Null(entries);
    }

    [Fact]
    public async Task GetModeAsync_ReturnsProfileMode()
    {
        var store = NewStore();
        await store.CreateAsync(new RouterProfile
        {
            Id = "",
            Name = "auto-profile",
            Mode = RouterProfileMode.Auto,
            Entries = [],
            CreatedAt = default,
            UpdatedAt = default,
        });
        await store.CreateAsync(new RouterProfile
        {
            Id = "",
            Name = "manual-profile",
            Mode = RouterProfileMode.Manual,
            Entries = [],
            CreatedAt = default,
            UpdatedAt = default,
        });

        var service = new RouterProfileService(store);

        var autoMode = await service.GetModeAsync("auto-profile");
        Assert.Equal(RouterProfileMode.Auto, autoMode);

        var manualMode = await service.GetModeAsync("manual-profile");
        Assert.Equal(RouterProfileMode.Manual, manualMode);
    }

    [Fact]
    public async Task GetModeAsync_ReturnsNullForUnknownProfile()
    {
        var service = NewService();
        var mode = await service.GetModeAsync("nonexistent");

        Assert.Null(mode);
    }

    [Fact]
    public async Task ListProfilesAsync_ReturnsAllProfiles()
    {
        var store = NewStore();
        await store.CreateAsync(new RouterProfile
        {
            Id = "",
            Name = "profile-a",
            Mode = RouterProfileMode.Auto,
            Entries = [],
            CreatedAt = default,
            UpdatedAt = default,
        });
        await store.CreateAsync(new RouterProfile
        {
            Id = "",
            Name = "profile-b",
            Mode = RouterProfileMode.Manual,
            Entries = [],
            CreatedAt = default,
            UpdatedAt = default,
        });

        var service = new RouterProfileService(store);
        var profiles = await service.ListProfilesAsync();

        Assert.Equal(2, profiles.Count);
        Assert.Equal("profile-a", profiles[0].Name);
        Assert.Equal("profile-b", profiles[1].Name);
    }

    [Fact]
    public async Task ResolveAsync_ActiveModelId_MovesEntryToFirst()
    {
        var store = NewStore();
        await store.CreateAsync(new RouterProfile
        {
            Id = "",
            Name = "active-profile",
            Mode = RouterProfileMode.Auto,
            Entries =
            [
                new RouterProfileEntry { ModelId = "model-a", Priority = 0, IsEnabled = true },
                new RouterProfileEntry { ModelId = "model-b", Priority = 1, IsEnabled = true },
                new RouterProfileEntry { ModelId = "model-c", Priority = 2, IsEnabled = true },
            ],
            CreatedAt = default,
            UpdatedAt = default,
        });
        var created = await store.GetByNameAsync("active-profile");
        await store.SetActiveModelIdAsync(created!.Id, "model-b");

        var service = new RouterProfileService(store);
        var result = await service.ResolveAsync("active-profile");

        Assert.NotNull(result);
        Assert.Equal(3, result!.Value.Entries.Count);
        Assert.Equal("model-b", result.Value.Entries[0].ModelId);
        Assert.Equal("model-a", result.Value.Entries[1].ModelId);
        Assert.Equal("model-c", result.Value.Entries[2].ModelId);
    }

    [Fact]
    public async Task ResolveAsync_ActiveModelId_NoMatch_FallsBackToPriority()
    {
        var store = NewStore();
        await store.CreateAsync(new RouterProfile
        {
            Id = "",
            Name = "no-match",
            Mode = RouterProfileMode.Auto,
            Entries =
            [
                new RouterProfileEntry { ModelId = "model-a", Priority = 0, IsEnabled = true },
                new RouterProfileEntry { ModelId = "model-b", Priority = 1, IsEnabled = true },
            ],
            CreatedAt = default,
            UpdatedAt = default,
        });
        var created = await store.GetByNameAsync("no-match");
        await store.SetActiveModelIdAsync(created!.Id, "model-nonexistent");

        var service = new RouterProfileService(store);
        var result = await service.ResolveAsync("no-match");

        Assert.NotNull(result);
        Assert.Equal(2, result!.Value.Entries.Count);
        Assert.Equal("model-a", result.Value.Entries[0].ModelId);
        Assert.Equal("model-b", result.Value.Entries[1].ModelId);
    }

    [Fact]
    public async Task ResolveAsync_ActiveModelId_Null_UsesPriorityOrder()
    {
        var store = NewStore();
        await store.CreateAsync(new RouterProfile
        {
            Id = "",
            Name = "null-active",
            Mode = RouterProfileMode.Auto,
            Entries =
            [
                new RouterProfileEntry { ModelId = "model-c", Priority = 2, IsEnabled = true },
                new RouterProfileEntry { ModelId = "model-a", Priority = 0, IsEnabled = true },
            ],
            ActiveModelId = null,
            CreatedAt = default,
            UpdatedAt = default,
        });

        var service = new RouterProfileService(store);
        var result = await service.ResolveAsync("null-active");

        Assert.NotNull(result);
        Assert.Equal(2, result!.Value.Entries.Count);
        Assert.Equal("model-a", result.Value.Entries[0].ModelId);
        Assert.Equal("model-c", result.Value.Entries[1].ModelId);
    }

    [Fact]
    public async Task ResolveAsync_ActiveModelId_DisabledEntry_FallsBackToPriority()
    {
        var store = NewStore();
        await store.CreateAsync(new RouterProfile
        {
            Id = "",
            Name = "disabled-active",
            Mode = RouterProfileMode.Auto,
            Entries =
            [
                new RouterProfileEntry { ModelId = "model-a", Priority = 0, IsEnabled = true },
                new RouterProfileEntry { ModelId = "model-b", Priority = 1, IsEnabled = false },
                new RouterProfileEntry { ModelId = "model-c", Priority = 2, IsEnabled = true },
            ],
            CreatedAt = default,
            UpdatedAt = default,
        });
        var created = await store.GetByNameAsync("disabled-active");
        await store.SetActiveModelIdAsync(created!.Id, "model-b");

        var service = new RouterProfileService(store);
        var result = await service.ResolveAsync("disabled-active");

        Assert.NotNull(result);
        Assert.Equal(2, result!.Value.Entries.Count);
        // Disabled entry is filtered out, so active match fails; priority order preserved
        Assert.Equal("model-a", result.Value.Entries[0].ModelId);
        Assert.Equal("model-c", result.Value.Entries[1].ModelId);
    }
}
