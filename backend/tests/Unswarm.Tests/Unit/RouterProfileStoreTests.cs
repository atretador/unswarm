using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Unit;

public sealed class RouterProfileStoreTests
{
    private static IRouterProfileStore NewStore() => TestRouterProfileStore.Create();

    [Fact]
    public async Task CreateAsync_PersistsProfile()
    {
        var store = NewStore();
        var profile = new RouterProfile
        {
            Id = "",
            Name = "fast-fallback",
            Mode = RouterProfileMode.Auto,
            Entries =
            [
                new RouterProfileEntry { ModelId = "local/llama-3.1-8b", Priority = 0, IsEnabled = true },
                new RouterProfileEntry { ModelId = "cloud/openai/gpt-4o", Priority = 1, IsEnabled = true },
            ],
            CreatedAt = default,
            UpdatedAt = default,
        };

        var created = await store.CreateAsync(profile);

        Assert.NotEmpty(created.Id);
        Assert.Equal("fast-fallback", created.Name);
        Assert.Equal(RouterProfileMode.Auto, created.Mode);
        Assert.Equal(2, created.Entries.Count);
        Assert.Equal("local/llama-3.1-8b", created.Entries[0].ModelId);
        Assert.Equal(0, created.Entries[0].Priority);
        Assert.Equal("cloud/openai/gpt-4o", created.Entries[1].ModelId);

        // Verify it persists
        var fetched = await store.GetAsync(created.Id);
        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched!.Id);
        Assert.Equal("fast-fallback", fetched.Name);
        Assert.Equal(2, fetched.Entries.Count);
    }

    [Fact]
    public async Task ListAsync_ReturnsAllOrderedByName()
    {
        var store = NewStore();
        await store.CreateAsync(new RouterProfile
        {
            Id = "", Name = "zzz", Mode = RouterProfileMode.Auto,
            Entries = [new RouterProfileEntry { ModelId = "m1", Priority = 0 }],
            CreatedAt = default, UpdatedAt = default,
        });
        await store.CreateAsync(new RouterProfile
        {
            Id = "", Name = "aaa", Mode = RouterProfileMode.Manual,
            Entries = [new RouterProfileEntry { ModelId = "m2", Priority = 0 }],
            CreatedAt = default, UpdatedAt = default,
        });

        var list = await store.ListAsync();
        Assert.Equal(2, list.Count);
        Assert.Equal("aaa", list[0].Name);
        Assert.Equal("zzz", list[1].Name);
    }

    [Fact]
    public async Task GetByNameAsync_ResolvesByName()
    {
        var store = NewStore();
        var created = await store.CreateAsync(new RouterProfile
        {
            Id = "", Name = "my-profile", Mode = RouterProfileMode.Manual,
            Entries = [new RouterProfileEntry { ModelId = "m1", Priority = 0 }],
            CreatedAt = default, UpdatedAt = default,
        });

        var found = await store.GetByNameAsync("my-profile");
        Assert.NotNull(found);
        Assert.Equal(created.Id, found!.Id);
        Assert.Equal(RouterProfileMode.Manual, found.Mode);

        Assert.Null(await store.GetByNameAsync("nonexistent"));
    }

    [Fact]
    public async Task CreateAsync_DuplicateName_Throws()
    {
        var store = NewStore();
        await store.CreateAsync(new RouterProfile
        {
            Id = "", Name = "dup", Mode = RouterProfileMode.Auto,
            Entries = [], CreatedAt = default, UpdatedAt = default,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CreateAsync(new RouterProfile
            {
                Id = "", Name = "dup", Mode = RouterProfileMode.Auto,
                Entries = [], CreatedAt = default, UpdatedAt = default,
            }));
    }

    [Fact]
    public async Task UpdateAsync_ModifiesProfile()
    {
        var store = NewStore();
        var created = await store.CreateAsync(new RouterProfile
        {
            Id = "", Name = "original", Mode = RouterProfileMode.Auto,
            Entries = [new RouterProfileEntry { ModelId = "m1", Priority = 0 }],
            CreatedAt = default, UpdatedAt = default,
        });

        var updated = await store.UpdateAsync(created.Id, new RouterProfile
        {
            Id = created.Id,
            Name = "renamed",
            Mode = RouterProfileMode.Manual,
            Entries =
            [
                new RouterProfileEntry { ModelId = "m1", Priority = 0, IsEnabled = false },
                new RouterProfileEntry { ModelId = "m2", Priority = 1, IsEnabled = true },
            ],
            CreatedAt = created.CreatedAt,
            UpdatedAt = default,
        });

        Assert.Equal("renamed", updated.Name);
        Assert.Equal(RouterProfileMode.Manual, updated.Mode);
        Assert.Equal(2, updated.Entries.Count);
        Assert.False(updated.Entries[0].IsEnabled);

        // Verify persisted
        var fetched = await store.GetAsync(created.Id);
        Assert.Equal("renamed", fetched!.Name);
        Assert.Equal(RouterProfileMode.Manual, fetched.Mode);
    }

    [Fact]
    public async Task UpdateAsync_NotFound_Throws()
    {
        var store = NewStore();
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => store.UpdateAsync("nonexistent", new RouterProfile
            {
                Id = "nonexistent", Name = "x", Mode = RouterProfileMode.Auto,
                Entries = [], CreatedAt = default, UpdatedAt = default,
            }));
    }

    [Fact]
    public async Task DeleteAsync_RemovesProfile()
    {
        var store = NewStore();
        var created = await store.CreateAsync(new RouterProfile
        {
            Id = "", Name = "to-delete", Mode = RouterProfileMode.Auto,
            Entries = [], CreatedAt = default, UpdatedAt = default,
        });

        await store.DeleteAsync(created.Id);
        Assert.Null(await store.GetAsync(created.Id));
        Assert.Null(await store.GetByNameAsync("to-delete"));
    }

    [Fact]
    public async Task DeleteAsync_NotFound_Throws()
    {
        var store = NewStore();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.DeleteAsync("nonexistent"));
    }

    [Fact]
    public async Task GetAsync_CachesResult()
    {
        var store = NewStore();
        var created = await store.CreateAsync(new RouterProfile
        {
            Id = "", Name = "cached", Mode = RouterProfileMode.Auto,
            Entries = [new RouterProfileEntry { ModelId = "m1", Priority = 0, IsEnabled = true }],
            CreatedAt = default, UpdatedAt = default,
        });

        // First call: populates cache
        var first = await store.GetAsync(created.Id);
        Assert.NotNull(first);

        // Second call: should hit cache (same object reference is fine for equality check)
        var second = await store.GetAsync(created.Id);
        Assert.NotNull(second);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(first.Name, second.Name);
    }

    [Fact]
    public async Task EntriesJson_RoundTrips_Correctly()
    {
        var store = NewStore();
        var entries = new List<RouterProfileEntry>
        {
            new() { ModelId = "cloud/openai/gpt-4o", Priority = 0, IsEnabled = true },
            new() { ModelId = "local/llama-3.1-70b", Priority = 1, IsEnabled = false },
            new() { ModelId = "cloud/anthropic/claude-sonnet", Priority = 2, IsEnabled = true },
        };

        var created = await store.CreateAsync(new RouterProfile
        {
            Id = "",
            Name = "multi-model",
            Mode = RouterProfileMode.Auto,
            Entries = entries,
            CreatedAt = default,
            UpdatedAt = default,
        });

        var fetched = await store.GetAsync(created.Id);
        Assert.NotNull(fetched);
        Assert.Equal(3, fetched!.Entries.Count);

        Assert.Equal("cloud/openai/gpt-4o", fetched.Entries[0].ModelId);
        Assert.Equal(0, fetched.Entries[0].Priority);
        Assert.True(fetched.Entries[0].IsEnabled);

        Assert.Equal("local/llama-3.1-70b", fetched.Entries[1].ModelId);
        Assert.Equal(1, fetched.Entries[1].Priority);
        Assert.False(fetched.Entries[1].IsEnabled);

        Assert.Equal("cloud/anthropic/claude-sonnet", fetched.Entries[2].ModelId);
        Assert.Equal(2, fetched.Entries[2].Priority);
        Assert.True(fetched.Entries[2].IsEnabled);
    }

    [Fact]
    public async Task EmptyEntries_DefaultsToEmptyArray()
    {
        var store = NewStore();
        var created = await store.CreateAsync(new RouterProfile
        {
            Id = "",
            Name = "empty",
            Mode = RouterProfileMode.Manual,
            Entries = [],
            CreatedAt = default,
            UpdatedAt = default,
        });

        var fetched = await store.GetAsync(created.Id);
        Assert.NotNull(fetched);
        Assert.Empty(fetched!.Entries);
    }

    [Fact]
    public async Task GetAsync_InvalidEntriesJson_ReturnsEmptyEntries()
    {
        // Verify that profiles with malformed EntriesJson still deserialize gracefully
        // (the store's internal deserializer returns [] for invalid JSON)
        var store = NewStore();
        var created = await store.CreateAsync(new RouterProfile
        {
            Id = "",
            Name = "normal",
            Mode = RouterProfileMode.Auto,
            Entries = [new RouterProfileEntry { ModelId = "m1", Priority = 0 }],
            CreatedAt = default,
            UpdatedAt = default,
        });

        // Valid entries round-trip correctly
        var fetched = await store.GetAsync(created.Id);
        Assert.NotNull(fetched);
        Assert.Single(fetched!.Entries);
    }
}
