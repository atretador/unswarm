using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Unswarm.Api.Controllers;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Unit;

public sealed class ApiKeyControllerTests
{
    private static IApiKeyStore NewStore() => TestApiKeyStore.Create();

    private static ApiKeyController CreateController(IApiKeyStore? store = null)
        => new(store ?? NewStore(), new StubCloudProviderStore(), new StubContainerRegistry(), new StubRouterProfileStore());

    /// <summary>Minimal ICloudProviderStore for controller tests: one configured provider.</summary>
    private sealed class StubCloudProviderStore : ICloudProviderStore
    {
        public Task CreateAsync(string name, string baseUrl, string apiKeyPlaintext, string apiKeyHint, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(string id, string baseUrl, string? apiKeyPlaintext, string? apiKeyHint, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<CloudProviderListItem>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CloudProviderListItem>>([
                new CloudProviderListItem { Id = "cp-1", Name = "openai" }
            ]);
        public Task<CloudProviderReadItem?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<CloudProviderReadItem?>(null);
        public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => Task.FromResult(false);
        public Task<string?> GetApiKeyAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SaveModelsAsync(string id, IReadOnlyList<string> modelIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CloudProviderReadItem?> GetByNameAsync(string name, CancellationToken ct = default) => Task.FromResult<CloudProviderReadItem?>(null);
        public Task<bool> NameExistsAsync(string name, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<string>> GetModelIdsAsync(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    /// <summary>Minimal IContainerRegistry for controller tests: no runtimes.</summary>
    private sealed class StubContainerRegistry : IContainerRegistry
    {
        public Task<IReadOnlyList<RegisteredRuntime>> ListAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RegisteredRuntime>>([]);
        public Task<RegisteredRuntime?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<RegisteredRuntime?>(null);
        public Task<RegisteredRuntime> CreateAsync(RegisteredRuntime container, CancellationToken ct = default) => Task.FromResult(container);
        public Task<RegisteredRuntime> UpdateAsync(string id, RegisteredRuntime container, CancellationToken ct = default) => Task.FromResult(container);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddModelMappingAsync(string registeredContainerId, string modelId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveModelMappingAsync(string registeredContainerId, string modelId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetModelIdsForContainerAsync(string registeredContainerId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> GetContainerIdForModelAsync(string modelName, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<(RegisteredRuntime A, RegisteredRuntime B)?> UpdateConcurrencyPairAsync(string idA, IReadOnlyList<string> newCanRunAlongWithA, string idB, IReadOnlyList<string> newCanRunAlongWithB, CancellationToken ct = default) => Task.FromResult<(RegisteredRuntime A, RegisteredRuntime B)?>(null);
    }

    /// <summary>Minimal IRouterProfileStore for controller tests: no profiles.</summary>
    private sealed class StubRouterProfileStore : IRouterProfileStore
    {
        public Task<IReadOnlyList<RouterProfile>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RouterProfile>>([]);
        public Task<RouterProfile?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<RouterProfile?>(null);
        public Task<RouterProfile?> GetByNameAsync(string name, CancellationToken ct = default) => Task.FromResult<RouterProfile?>(null);
        public Task<RouterProfile> CreateAsync(RouterProfile profile, CancellationToken ct = default) => Task.FromResult(profile);
        public Task<RouterProfile> UpdateAsync(string id, RouterProfile profile, CancellationToken ct = default) => Task.FromResult(profile);
        public Task SetActiveModelIdAsync(string id, string? activeModelId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ── Inference create ──────────────────────────────────────────────

    [Fact]
    public async Task Create_InferenceKey_ReturnsSecretWithUskPrefixAndScopeInference()
    {
        var ctrl = CreateController();
        var result = await ctrl.Create(new CreateApiKeyRequest("ci key"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiKeyCreateResponse>(ok.Value);

        Assert.StartsWith("usk_", resp.Secret);
        Assert.Equal(ApiKeyScope.Inference, resp.Scope);
        Assert.Equal("ci key", resp.Name);
        Assert.True(resp.IsActive);
    }

    // ── Agent create ──────────────────────────────────────────────────

    [Fact]
    public async Task CreateAgent_AgentKey_ReturnsScopeAgent()
    {
        var ctrl = CreateController();
        var result = await ctrl.CreateAgent(new CreateApiKeyRequest("agent-prod"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiKeyCreateResponse>(ok.Value);

        Assert.Equal("agent-prod", resp.Name);
        Assert.Equal(ApiKeyScope.Agent, resp.Scope);
        Assert.StartsWith("ak_", resp.Secret);
        Assert.True(resp.IsActive);
    }

    // ── Name validation ───────────────────────────────────────────────

    [Fact]
    public async Task Create_BlankName_ReturnsBadRequest()
    {
        var ctrl = CreateController();
        var result = await ctrl.Create(new CreateApiKeyRequest("  "), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CreateAgent_BlankName_ReturnsBadRequest()
    {
        var ctrl = CreateController();
        var result = await ctrl.CreateAgent(new CreateApiKeyRequest("  "), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Agent key binding (boundAgentName) ────────────────────────────

    [Fact]
    public async Task CreateAgent_WithBoundAgentName_ReturnsAndPersistsBinding()
    {
        var store = NewStore();
        var ctrl = CreateController(store);

        var result = await ctrl.CreateAgent(
            new CreateApiKeyRequest("alpha key", BoundAgentName: "alpha"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiKeyCreateResponse>(ok.Value);
        Assert.Equal("alpha", resp.BoundAgentName);

        // Binding persisted; the secret authenticates to the bound entity.
        var entity = await store.AuthenticateAsync(resp.Secret);
        Assert.NotNull(entity);
        Assert.Equal("alpha", entity!.BoundAgentName);
    }

    [Fact]
    public async Task CreateAgent_WithoutBoundAgentName_StaysUnbound()
    {
        var ctrl = CreateController();
        var result = await ctrl.CreateAgent(new CreateApiKeyRequest("free"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiKeyCreateResponse>(ok.Value);
        Assert.Null(resp.BoundAgentName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAgent_BlankBoundAgentName_ReturnsBadRequest(string boundAgentName)
    {
        var ctrl = CreateController();
        var result = await ctrl.CreateAgent(
            new CreateApiKeyRequest("bad", BoundAgentName: boundAgentName), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task List_IncludesBoundAgentName()
    {
        var store = NewStore();
        var ctrl = CreateController(store);

        await ctrl.Create(new CreateApiKeyRequest("inf-key"), CancellationToken.None);
        await ctrl.CreateAgent(new CreateApiKeyRequest("ag-key", BoundAgentName: "alpha"), CancellationToken.None);

        var result = await ctrl.List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<IEnumerable<ApiKeyListItem>>(ok.Value).ToList();

        Assert.Null(items.Single(i => i.Name == "inf-key").BoundAgentName);
        Assert.Equal("alpha", items.Single(i => i.Name == "ag-key").BoundAgentName);
    }

    // ── Stored-hash authenticates ─────────────────────────────────────

    [Fact]
    public async Task Create_ThenAuthenticateAsync_SecretIsValidated()
    {
        var store = NewStore();
        var ctrl = CreateController(store);

        var result = await ctrl.Create(new CreateApiKeyRequest("auth-test"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiKeyCreateResponse>(ok.Value);

        // The raw secret must authenticate against the real store's stored hash.
        var entity = await store.AuthenticateAsync(resp.Secret);
        Assert.NotNull(entity);
        Assert.Equal(resp.Id, entity!.Id);
        Assert.Equal("auth-test", entity.Name);
        Assert.Equal(ApiKeyScope.Inference, entity.Scope);
    }

    // ── JSON serialization (lowercase scope) ──────────────────────────

    [Fact]
    public async Task CreateAgent_ResponseJson_ContainsLowercaseScopeAgent()
    {
        var ctrl = CreateController();
        var result = await ctrl.CreateAgent(new CreateApiKeyRequest("json-test"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiKeyCreateResponse>(ok.Value);

        // Serialize through the property-level converter on the DTO.
        var json = JsonSerializer.Serialize(resp, JsonOptions);

        Assert.Contains("\"scope\":\"agent\"", json);
        Assert.DoesNotContain("\"scope\":\"Agent\"", json);
    }

    // ── List returns both scopes ──────────────────────────────────────

    [Fact]
    public async Task List_ReturnsBothScopes()
    {
        var store = NewStore();
        var ctrl = CreateController(store);

        await ctrl.Create(new CreateApiKeyRequest("inf-key"), CancellationToken.None);
        await ctrl.CreateAgent(new CreateApiKeyRequest("ag-key"), CancellationToken.None);

        var result = await ctrl.List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<IEnumerable<ApiKeyListItem>>(ok.Value).ToList();

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Scope == ApiKeyScope.Inference && i.Name == "inf-key");
        Assert.Contains(items, i => i.Scope == ApiKeyScope.Agent && i.Name == "ag-key");
    }

    // ── Revoke ────────────────────────────────────────────────────────

    [Fact]
    public async Task Revoke_ExistingKey_ReturnsNoContentAndDeactivates()
    {
        var store = NewStore();
        var ctrl = CreateController(store);

        var created = await store.CreateAsync("to-revoke", ApiKeyScope.Inference);

        var result = await ctrl.Revoke(created.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var item = await store.GetAsync(created.Id);
        Assert.NotNull(item);
        Assert.False(item!.IsActive);
    }

    // ── Rotate reveals new secret ─────────────────────────────────────

    [Fact]
    public async Task Rotate_ExistingKey_RevealsNewSecret()
    {
        var store = NewStore();
        var ctrl = CreateController(store);

        var created = await store.CreateAsync("rotatable", ApiKeyScope.Agent);

        var result = await ctrl.Rotate(created.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiKeyCreateResponse>(ok.Value);

        Assert.Equal(created.Id, resp.Id);
        Assert.Equal("rotatable", resp.Name);
        Assert.Equal(ApiKeyScope.Agent, resp.Scope);
        Assert.StartsWith("ak_", resp.Secret);
        Assert.NotEqual(created.Secret, resp.Secret);
    }

    // ── Unknown id → 404 ─────────────────────────────────────────────

    [Fact]
    public async Task Rotate_UnknownId_ReturnsNotFound()
    {
        var ctrl = CreateController();

        var result = await ctrl.Rotate("nonexistent", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }
}
