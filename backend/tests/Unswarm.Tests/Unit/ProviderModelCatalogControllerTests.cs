using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Controllers;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class ProviderModelCatalogControllerTests
{
    private readonly FakeCloudProviderStore _cloudProviders = new();
    private readonly FakeContainerRegistry _containers = new();
    private readonly FakeRouterProfileStore _routerProfiles = new();
    private readonly FakeModelRegistry _modelRegistry = new();

    private ProviderModelCatalogController CreateController(
        FakeCloudProviderStore? cloudProviders = null,
        FakeContainerRegistry? containers = null,
        FakeRouterProfileStore? routerProfiles = null,
        FakeModelRegistry? modelRegistry = null)
        => new(
            cloudProviders ?? _cloudProviders,
            containers ?? _containers,
            routerProfiles ?? _routerProfiles,
            modelRegistry ?? _modelRegistry);

    // ── Fakes ────────────────────────────────────────────────────────

    private sealed class FakeCloudProviderStore : ICloudProviderStore
    {
        private readonly List<CloudProviderListItem> _items = [];
        private readonly Dictionary<string, IReadOnlyList<string>> _modelIds = new();
        private readonly Dictionary<string, CloudProviderReadItem?> _readItems = new();
        private readonly Dictionary<string, CloudProviderReadItem?> _byName = new();

        public List<CloudProviderListItem> Items => _items;

        public void AddProvider(string id, string name, params string[] modelIds)
        {
            _items.Add(new CloudProviderListItem { Id = id, Name = name });
            _modelIds[id] = modelIds.ToList();
            _readItems[id] = new CloudProviderReadItem { Id = id, Name = name };
            _byName[name] = new CloudProviderReadItem { Id = id, Name = name };
        }

        public Task<IReadOnlyList<CloudProviderListItem>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CloudProviderListItem>>(_items);

        public Task<CloudProviderReadItem?> GetAsync(string id, CancellationToken ct = default)
        {
            _readItems.TryGetValue(id, out var item);
            return Task.FromResult(item);
        }

        public Task<CloudProviderReadItem?> GetByNameAsync(string name, CancellationToken ct = default)
        {
            _byName.TryGetValue(name, out var item);
            return Task.FromResult(item);
        }

        public Task<IReadOnlyList<string>> GetModelIdsAsync(string id, CancellationToken ct = default)
        {
            _modelIds.TryGetValue(id, out var models);
            return Task.FromResult<IReadOnlyList<string>>(models ?? []);
        }

        public Task CreateAsync(string name, string baseUrl, string apiKeyPlaintext, string apiKeyHint, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateAsync(string name, string baseUrl, string? apiKeyPlaintext, string apiKeyHint, int authType, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(string id, string baseUrl, string? apiKeyPlaintext, string? apiKeyHint, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => Task.FromResult(false);
        public Task<string?> GetApiKeyAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SaveModelsAsync(string id, IReadOnlyList<string> modelIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> NameExistsAsync(string name, CancellationToken ct = default) => Task.FromResult(false);
        public Task SaveOAuthTokensAsync(string id, string accessTokenCiphertext, string refreshTokenCiphertext, DateTimeOffset? expiresAt, string? chatgptAccountId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<OAuthTokenSet?> GetOAuthTokensAsync(string id, CancellationToken ct = default) => Task.FromResult<OAuthTokenSet?>(null);
        public Task<int> GetAuthTypeAsync(string id, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class FakeRouterProfileStore : IRouterProfileStore
    {
        private readonly List<RouterProfile> _profiles = [];

        public List<RouterProfile> Profiles => _profiles;

        public void AddProfile(string id, string name, params RouterProfileEntry[] entries)
        {
            _profiles.Add(new RouterProfile
            {
                Id = id,
                Name = name,
                Entries = entries,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        public Task<IReadOnlyList<RouterProfile>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RouterProfile>>(_profiles);

        public Task<RouterProfile?> GetAsync(string id, CancellationToken ct = default)
            => Task.FromResult(_profiles.FirstOrDefault(p => p.Id == id));

        public Task<RouterProfile?> GetByNameAsync(string name, CancellationToken ct = default)
            => Task.FromResult(_profiles.FirstOrDefault(p => p.Name == name));

        public Task<RouterProfile> CreateAsync(RouterProfile profile, CancellationToken ct = default) => Task.FromResult(profile);
        public Task<RouterProfile> UpdateAsync(string id, RouterProfile profile, CancellationToken ct = default) => Task.FromResult(profile);
        public Task SetActiveModelIdAsync(string id, string? activeModelId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RouterProfile> SetThinkingEffortAsync(string id, string modelId, string? thinkingEffortOverride, CancellationToken ct = default)
            => Task.FromResult(new RouterProfile { Id = id, Name = "", Mode = RouterProfileMode.Auto, Entries = [], CreatedAt = default, UpdatedAt = default });
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ── Tests ────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ReturnsOkWithCatalogItems()
    {
        var ctrl = CreateController();
        var result = await ctrl.Get(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<List<ProviderModelCatalogItem>>(ok.Value);
        Assert.NotNull(items);
    }

    [Fact]
    public async Task Get_IncludesCloudProviders()
    {
        var cloudProviders = new FakeCloudProviderStore();
        cloudProviders.AddProvider("cp-1", "openai", "gpt-4o", "gpt-4o-mini");
        var ctrl = CreateController(cloudProviders: cloudProviders);

        var result = await ctrl.Get(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<List<ProviderModelCatalogItem>>(ok.Value);

        var cloud = Assert.Single(items, i => i.Kind == "cloud");
        Assert.Equal("openai", cloud.Name);
        Assert.Equal(2, cloud.Models.Count);
        Assert.Contains("gpt-4o", cloud.Models);
        Assert.Contains("gpt-4o-mini", cloud.Models);
        // Display names for cloud models equal the model ID itself
        Assert.Equal("gpt-4o", cloud.ModelDisplayNames["gpt-4o"]);
        Assert.Equal("gpt-4o-mini", cloud.ModelDisplayNames["gpt-4o-mini"]);
    }

    [Fact]
    public async Task Get_IncludesLocalRuntimes()
    {
        var containers = new FakeContainerRegistry();
        var runtime = await containers.CreateAsync(new RegisteredRuntime
        {
            Id = "rt-1",
            DisplayName = "llama-server",
            Image = "llama:latest"
        });
        await containers.AddModelMappingAsync("rt-1", "llama-7b");

        var modelRegistry = new FakeModelRegistry();
        await modelRegistry.CreateAsync(new ModelDefinition
        {
            Id = "llama-7b",
            Name = "llama-7b",
            DisplayName = "LLaMA 7B"
        });

        var ctrl = CreateController(containers: containers, modelRegistry: modelRegistry);

        var result = await ctrl.Get(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<List<ProviderModelCatalogItem>>(ok.Value);

        var local = Assert.Single(items, i => i.Kind == "local");
        Assert.Equal("llama-server", local.Name);
        Assert.Contains("llama-7b", local.Models);
        Assert.Equal("LLaMA 7B", local.ModelDisplayNames["llama-7b"]);
    }

    [Fact]
    public async Task Get_IncludesRouterProfiles()
    {
        var routerProfiles = new FakeRouterProfileStore();
        routerProfiles.AddProfile("rp-1", "my-router",
            new RouterProfileEntry { ModelId = "cloud/openai/gpt-4o", Priority = 0, IsEnabled = true },
            new RouterProfileEntry { ModelId = "llama-7b", Priority = 1, IsEnabled = true },
            new RouterProfileEntry { ModelId = "disabled-model", Priority = 2, IsEnabled = false });

        var modelRegistry = new FakeModelRegistry();
        await modelRegistry.CreateAsync(new ModelDefinition
        {
            Id = "llama-7b",
            Name = "llama-7b",
            DisplayName = "LLaMA 7B"
        });

        var ctrl = CreateController(routerProfiles: routerProfiles, modelRegistry: modelRegistry);

        var result = await ctrl.Get(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<List<ProviderModelCatalogItem>>(ok.Value);

        var router = Assert.Single(items, i => i.Kind == "router");
        Assert.Equal("my-router", router.Name);
        // Disabled entries are filtered out; only the two enabled models remain
        Assert.Equal(2, router.Models.Count);
        Assert.Equal("cloud/openai/gpt-4o", router.Models[0]); // lower priority first
        Assert.Equal("llama-7b", router.Models[1]);
        // Cloud model display name is the model ID itself
        Assert.Equal("cloud/openai/gpt-4o", router.ModelDisplayNames["cloud/openai/gpt-4o"]);
        // Local model display name comes from the registry
        Assert.Equal("LLaMA 7B", router.ModelDisplayNames["llama-7b"]);
    }

    [Fact]
    public async Task Get_EmptyDependencies_ReturnsEmptyList()
    {
        var ctrl = CreateController();

        var result = await ctrl.Get(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<List<ProviderModelCatalogItem>>(ok.Value);
        Assert.Empty(items);
    }
}
