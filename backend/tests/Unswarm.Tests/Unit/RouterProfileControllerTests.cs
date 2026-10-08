using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Unswarm.Api.Controllers;
using Unswarm.Api.Dtos;
using Unswarm.Api.Services;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;
using Unswarm.Core.Services;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

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

        public Task<IReadOnlyList<string>> GetAllContainerIdsForModelAsync(string modelName)
        {
            IReadOnlyList<string> ids = _modelMap
                .Where(kv => kv.Key == modelName || kv.Key.EndsWith(":" + modelName))
                .Select(kv => kv.Value.ContainerId)
                .Distinct()
                .ToList();
            return Task.FromResult(ids);
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

    // ── Status endpoint ────────────────────────────────────────────────

    private sealed class StubLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }

    private static RouterProfileController CreateController(IRouterProfileStore? store = null, RouterProfileActivityTracker? tracker = null)
        => new(store ?? TestRouterProfileStore.Create(), new StubLogger<RouterProfileController>(), tracker ?? new RouterProfileActivityTracker());

    [Fact]
    public void GetStatus_ReturnsActiveCounts()
    {
        var tracker = new RouterProfileActivityTracker();
        tracker.Increment("Executor");
        tracker.Increment("Executor");
        tracker.Increment("Fast");

        var ctrl = CreateController(tracker: tracker);
        var result = ctrl.GetStatus();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dict = Assert.IsAssignableFrom<IReadOnlyDictionary<string, int>>(ok.Value);
        Assert.Equal(2, dict.Count);
        Assert.Equal(2, dict["Executor"]);
        Assert.Equal(1, dict["Fast"]);
    }

    [Fact]
    public void GetStatus_EmptyWhenNoActive()
    {
        var tracker = new RouterProfileActivityTracker();
        var ctrl = CreateController(tracker: tracker);
        var result = ctrl.GetStatus();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dict = Assert.IsAssignableFrom<IReadOnlyDictionary<string, int>>(ok.Value);
        Assert.Empty(dict);
    }

    // ── Get ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ExistingProfile_ReturnsDto()
    {
        var store = TestRouterProfileStore.Create();
        var created = await store.CreateAsync(new RouterProfile
        {
            Id = "",
            Name = "Executor",
            Mode = RouterProfileMode.Auto,
            Entries = [new RouterProfileEntry { ModelId = "llama-3", Priority = 0 }]
        });

        var result = await CreateController(store).Get(created.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<RouterProfileDto>(ok.Value);
        Assert.Equal(created.Id, dto.Id);
        Assert.Equal("Executor", dto.Name);
        var entry = Assert.Single(dto.Entries);
        Assert.Equal("llama-3", entry.ModelId);
    }

    [Fact]
    public async Task Get_MissingProfile_ReturnsNotFound()
    {
        var result = await CreateController().Get("nope", CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── SetThinkingEffort ──────────────────────────────────────────────

    [Fact]
    public async Task SetThinkingEffort_ExistingEntry_ReturnsUpdatedProfile()
    {
        var store = TestRouterProfileStore.Create();
        var created = await store.CreateAsync(new RouterProfile
        {
            Id = "",
            Name = "Executor",
            Mode = RouterProfileMode.Auto,
            Entries = [new RouterProfileEntry { ModelId = "llama-3", Priority = 0 }]
        });

        var result = await CreateController(store).SetThinkingEffort(created.Id,
            new SetThinkingEffortRequest { ModelId = "llama-3", ThinkingEffortOverride = "high" },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<RouterProfileDto>(ok.Value);
        var entry = Assert.Single(dto.Entries);
        Assert.Equal("high", entry.ThinkingEffortOverride);
    }

    [Fact]
    public async Task SetThinkingEffort_MissingProfile_ReturnsNotFound()
    {
        var result = await CreateController().SetThinkingEffort("nope",
            new SetThinkingEffortRequest { ModelId = "llama-3", ThinkingEffortOverride = "high" },
            CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── List ───────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ReturnsAllProfiles()
    {
        var store = TestRouterProfileStore.Create();
        await store.CreateAsync(new RouterProfile { Id = "", Name = "Alpha" });
        await store.CreateAsync(new RouterProfile { Id = "", Name = "Beta" });

        var result = await CreateController(store).List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dtos = Assert.IsAssignableFrom<IEnumerable<RouterProfileDto>>(ok.Value).ToList();
        Assert.Equal(2, dtos.Count);
        Assert.Contains(dtos, d => d.Name == "Alpha");
        Assert.Contains(dtos, d => d.Name == "Beta");
    }

    // ── Create ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_Valid_ReturnsOkAndPersists()
    {
        var store = TestRouterProfileStore.Create();
        var request = new CreateRouterProfileRequest(
            "Executor",
            RouterProfileMode.Manual,
            [new RouterProfileEntryDto { ModelId = "llama-3", Priority = 0 }]);

        var result = await CreateController(store).Create(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<RouterProfileDto>(ok.Value);
        Assert.Equal("Executor", dto.Name);
        Assert.Equal(RouterProfileMode.Manual, dto.Mode);
        Assert.Single(dto.Entries);
        Assert.Contains(await store.ListAsync(), p => p.Name == "Executor");
    }

    [Fact]
    public async Task Create_BlankName_ReturnsBadRequest()
    {
        var result = await CreateController().Create(
            new CreateRouterProfileRequest("  "), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Create_DuplicateName_ReturnsConflict()
    {
        var store = TestRouterProfileStore.Create();
        await store.CreateAsync(new RouterProfile { Id = "", Name = "Executor" });

        var result = await CreateController(store).Create(
            new CreateRouterProfileRequest("Executor"), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
    }

    // ── Update ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_Valid_ReturnsOkWithNewValues()
    {
        var store = TestRouterProfileStore.Create();
        var created = await store.CreateAsync(new RouterProfile { Id = "", Name = "Old" });

        var result = await CreateController(store).Update(created.Id,
            new UpdateRouterProfileRequest("New", RouterProfileMode.Manual,
                [new RouterProfileEntryDto { ModelId = "llama-3" }]),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<RouterProfileDto>(ok.Value);
        Assert.Equal("New", dto.Name);
        Assert.Equal(RouterProfileMode.Manual, dto.Mode);
    }

    [Fact]
    public async Task Update_UnknownId_ReturnsNotFound()
    {
        var result = await CreateController().Update("nope",
            new UpdateRouterProfileRequest("X", RouterProfileMode.Auto, []), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Update_BlankName_ReturnsBadRequest()
    {
        var result = await CreateController().Update("any",
            new UpdateRouterProfileRequest(" ", RouterProfileMode.Auto, []), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Update_NameCollision_ReturnsConflict()
    {
        var store = TestRouterProfileStore.Create();
        await store.CreateAsync(new RouterProfile { Id = "", Name = "Taken" });
        var other = await store.CreateAsync(new RouterProfile { Id = "", Name = "Mine" });

        var result = await CreateController(store).Update(other.Id,
            new UpdateRouterProfileRequest("Taken", RouterProfileMode.Auto, []), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
    }

    // ── Delete ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_Existing_ReturnsNoContent()
    {
        var store = TestRouterProfileStore.Create();
        var created = await store.CreateAsync(new RouterProfile { Id = "", Name = "Doomed" });

        var result = await CreateController(store).Delete(created.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await store.GetAsync(created.Id));
    }

    [Fact]
    public async Task Delete_Unknown_ReturnsNotFound()
    {
        var result = await CreateController().Delete("nope", CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── SetActiveEntry ─────────────────────────────────────────────────

    [Fact]
    public async Task SetActiveEntry_ExistingProfile_ReturnsUpdatedProfile()
    {
        var store = TestRouterProfileStore.Create();
        var created = await store.CreateAsync(new RouterProfile
        {
            Id = "",
            Name = "Executor",
            Entries = [new RouterProfileEntry { ModelId = "llama-3", Priority = 0 }]
        });

        var result = await CreateController(store).SetActiveEntry(created.Id,
            new SetActiveEntryRequest { ActiveModelId = "llama-3" }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<RouterProfileDto>(ok.Value);
        Assert.Equal("llama-3", dto.ActiveModelId);
    }

    [Fact]
    public async Task SetActiveEntry_UnknownProfile_ReturnsNotFound()
    {
        var result = await CreateController().SetActiveEntry("nope",
            new SetActiveEntryRequest { ActiveModelId = "llama-3" }, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }
}
