using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;
using Unswarm.Core.Services;

namespace Unswarm.Tests.Unit;

public sealed class RouterProfileControllerTests
{
    /// <summary>
    /// Minimal IContainerRegistry that can resolve model → container mappings.
    /// </summary>
    private sealed class StubContainerRegistry : IContainerRegistry
    {
        private readonly Dictionary<string, (string ContainerId, string DisplayName)> _modelMap;

        public StubContainerRegistry(Dictionary<string, (string ContainerId, string DisplayName)>? modelMap = null)
        {
            _modelMap = modelMap ?? new(StringComparer.OrdinalIgnoreCase);
        }

        public Task<IReadOnlyList<RegisteredRuntime>> ListAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RegisteredRuntime>>([]);

        public Task<RegisteredRuntime?> GetAsync(string id, CancellationToken ct = default)
        {
            var match = _modelMap.Values.FirstOrDefault(v => v.ContainerId == id);
            if (match.ContainerId is null)
                return Task.FromResult<RegisteredRuntime?>(null);
            return Task.FromResult<RegisteredRuntime?>(new RegisteredRuntime
            {
                Id = id,
                Image = "stub-image",
                DisplayName = match.DisplayName,
            });
        }

        public Task<RegisteredRuntime> CreateAsync(RegisteredRuntime container, CancellationToken ct = default) => Task.FromResult(container);
        public Task<RegisteredRuntime> UpdateAsync(string id, RegisteredRuntime container, CancellationToken ct = default) => Task.FromResult(container);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddModelMappingAsync(string registeredContainerId, string modelId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveModelMappingAsync(string registeredContainerId, string modelId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetModelIdsForContainerAsync(string registeredContainerId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> GetContainerIdForModelAsync(string modelName, CancellationToken ct = default)
        {
            return Task.FromResult(_modelMap.TryGetValue(modelName, out var m) ? m.ContainerId : null);
        }

        public Task<(RegisteredRuntime A, RegisteredRuntime B)?> UpdateConcurrencyPairAsync(string idA, IReadOnlyList<string> newCanRunAlongWithA, string idB, IReadOnlyList<string> newCanRunAlongWithB, CancellationToken ct = default) => Task.FromResult<(RegisteredRuntime A, RegisteredRuntime B)?>(null);
    }

    private static (ApiKeyAccessService AccessService, IApiKeyStore Store) CreateAccessService(
        Dictionary<string, (string ContainerId, string DisplayName)>? modelMap = null)
    {
        // Build an in-memory SQLite store for the access service's KeyAccess reads.
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<UnswarmDbContext>()
            .UseSqlite(connection)
            .Options;
        using (var db = new UnswarmDbContext(options))
        {
            db.Database.EnsureCreated();
        }

        var factory = () => new UnswarmDbContext(options);
        var registry = new StubContainerRegistry(modelMap);
        var store = TestApiKeyStore.Create();
        var accessService = new ApiKeyAccessService(factory, registry, keyStore: store);
        return (accessService, store);
    }

    // ── Router profile access: allowed via Providers ────────────────────

    [Fact]
    public async Task IsModelAllowed_RouterProfileInProviders_ReturnsTrue()
    {
        var (accessService, store) = CreateAccessService();
        var key = await store.CreateAsync("test-key", ApiKeyScope.Inference);
        await store.SaveAccessAsync(key.Id, new KeyAccess
        {
            Providers = ["my-router"],
            Models = [],
        });

        var allowed = await accessService.IsModelAllowedAsync(key.Id, "router/my-router");

        Assert.True(allowed);
    }

    // ── Router profile access: allowed via Models ──────────────────────

    [Fact]
    public async Task IsModelAllowed_RouterProfileInModels_ReturnsTrue()
    {
        var (accessService, store) = CreateAccessService();
        var key = await store.CreateAsync("test-key", ApiKeyScope.Inference);
        await store.SaveAccessAsync(key.Id, new KeyAccess
        {
            Providers = [],
            Models = ["router/my-router"],
        });

        var allowed = await accessService.IsModelAllowedAsync(key.Id, "router/my-router");

        Assert.True(allowed);
    }

    // ── Router profile access: denied when unknown ─────────────────────

    [Fact]
    public async Task IsModelAllowed_UnknownRouterProfile_ReturnsFalse()
    {
        var (accessService, store) = CreateAccessService();
        var key = await store.CreateAsync("test-key", ApiKeyScope.Inference);
        await store.SaveAccessAsync(key.Id, new KeyAccess
        {
            Providers = ["other-profile"],
            Models = [],
        });

        var allowed = await accessService.IsModelAllowedAsync(key.Id, "router/unknown-profile");

        Assert.False(allowed);
    }

    // ── Router profile access: unrestricted key allows router models ──

    [Fact]
    public async Task IsModelAllowed_UnrestrictedKey_RouterModelAllowed()
    {
        var (accessService, store) = CreateAccessService();
        var key = await store.CreateAsync("test-key", ApiKeyScope.Inference);
        // No access restrictions set → unrestricted

        var allowed = await accessService.IsModelAllowedAsync(key.Id, "router/any-profile");

        Assert.True(allowed);
    }

    // ── FilterModelsAsync: filters router models correctly ─────────────

    [Fact]
    public async Task FilterModels_RouterModels_OnlyAllowedReturned()
    {
        var (accessService, store) = CreateAccessService();
        var key = await store.CreateAsync("test-key", ApiKeyScope.Inference);
        await store.SaveAccessAsync(key.Id, new KeyAccess
        {
            Providers = ["allowed-router"],
            Models = ["router/explicit-model"],
        });

        var candidates = new[]
        {
            "router/allowed-router",
            "router/explicit-model",
            "router/denied-router",
            "cloud/openai/gpt-4o",
        };

        var filtered = await accessService.FilterModelsAsync(key.Id, candidates);

        Assert.Equal(2, filtered.Count);
        Assert.Contains("router/allowed-router", filtered);
        Assert.Contains("router/explicit-model", filtered);
        Assert.DoesNotContain("router/denied-router", filtered);
        Assert.DoesNotContain("cloud/openai/gpt-4o", filtered);
    }

    // ── FilterModelsAsync: unrestricted key returns all router models ──

    [Fact]
    public async Task FilterModels_UnrestrictedKey_AllRouterModelsReturned()
    {
        var (accessService, store) = CreateAccessService();
        var key = await store.CreateAsync("test-key", ApiKeyScope.Inference);
        // No access restrictions → unrestricted

        var candidates = new[]
        {
            "router/profile-a",
            "router/profile-b",
            "cloud/openai/gpt-4o",
        };

        var filtered = await accessService.FilterModelsAsync(key.Id, candidates);

        Assert.Equal(3, filtered.Count);
    }

    // ── Router profile name case-insensitive match ─────────────────────

    [Fact]
    public async Task IsModelAllowed_RouterProfile_CaseInsensitiveMatch()
    {
        var (accessService, store) = CreateAccessService();
        var key = await store.CreateAsync("test-key", ApiKeyScope.Inference);
        await store.SaveAccessAsync(key.Id, new KeyAccess
        {
            Providers = ["MyRouter"],
            Models = [],
        });

        var allowed = await accessService.IsModelAllowedAsync(key.Id, "router/myrouter");

        Assert.True(allowed);
    }
}
