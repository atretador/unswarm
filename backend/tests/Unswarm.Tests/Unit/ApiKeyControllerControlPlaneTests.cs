using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using Unswarm.Api.Controllers;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;

namespace Unswarm.Tests.Unit;

public sealed class ApiKeyControllerControlPlaneTests
{
    private static ApiKeyController CreateController(StubApiKeyStore? store = null)
        => new(store ?? new StubApiKeyStore(), new StubCloudProviders(), new StubContainers(),
            new StubRouterProfiles(), new LoggerFactory().CreateLogger<ApiKeyController>());

    [Fact]
    public async Task CreateControlPlane_CreatesKeyWithPermissionsAndSecret()
    {
        var store = new StubApiKeyStore();
        var result = await CreateController(store).CreateControlPlane(
            new CreateControlPlaneKeyRequest("cli", new() { ["users"] = "rw" }), CancellationToken.None);

        var response = Assert.IsType<ApiKeyCreateResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(ApiKeyScope.ControlPlane, response.Scope);
        Assert.Equal("cli", response.Name);
        Assert.NotEmpty(response.Secret);
        Assert.Equal("rw", store.Permissions[response.Id]["users"]);
    }

    [Fact]
    public async Task CreateControlPlane_MissingName_ReturnsBadRequest()
    {
        var result = await CreateController().CreateControlPlane(
            new CreateControlPlaneKeyRequest(" ", new()), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CreateControlPlane_SavesPermissionsJson()
    {
        var store = new StubApiKeyStore();
        await CreateController(store).CreateControlPlane(
            new CreateControlPlaneKeyRequest("cli", new() { ["models"] = "r", ["users"] = "none" }), CancellationToken.None);

        Assert.Equal(new Dictionary<string, string> { ["models"] = "r", ["users"] = "none" },
            store.Permissions.Values.Single());
    }

    [Fact]
    public async Task GetPermissions_ExistingKey_ReturnsPermissions()
    {
        var store = new StubApiKeyStore();
        var key = await store.CreateAsync("cli", ApiKeyScope.ControlPlane, permissionsJson: "{\"users\":\"rw\"}");

        var result = await CreateController(store).GetPermissions(key.Id, CancellationToken.None);

        var dto = Assert.IsType<ApiKeyPermissionsDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("rw", dto.Permissions["users"]);
    }

    [Fact]
    public async Task GetPermissions_MissingKey_ReturnsNotFound()
        => Assert.IsType<NotFoundObjectResult>(await CreateController().GetPermissions("missing", CancellationToken.None));

    [Fact]
    public async Task GetPermissions_NonControlPlaneKey_ReturnsBadRequest()
    {
        var store = new StubApiKeyStore();
        var key = await store.CreateAsync("inf", ApiKeyScope.Inference);

        Assert.IsType<BadRequestObjectResult>(await CreateController(store).GetPermissions(key.Id, CancellationToken.None));
    }

    [Fact]
    public async Task UpdatePermissions_SavesNewPermissions()
    {
        var store = new StubApiKeyStore();
        var key = await store.CreateAsync("cli", ApiKeyScope.ControlPlane);
        var request = new ApiKeyPermissionsDto { Permissions = new() { ["settings"] = "r" } };

        var result = await CreateController(store).SavePermissions(key.Id, request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("r", store.Permissions[key.Id]["settings"]);
    }

    [Fact]
    public async Task UpdatePermissions_BlocksTheCallingKey()
    {
        var store = new StubApiKeyStore();
        var key = await store.CreateAsync("cli", ApiKeyScope.ControlPlane);
        var controller = CreateController(store);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("unswarm:key-id", key.Id)], "ApiKey"))
            }
        };

        var result = await controller.SavePermissions(key.Id,
            new ApiKeyPermissionsDto { Permissions = new() { ["users"] = "rw" } },
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(store.Permissions[key.Id]);
    }

    [Fact]
    public async Task UpdatePermissions_MissingKey_ReturnsNotFound()
    {
        var request = new ApiKeyPermissionsDto { Permissions = new() { ["users"] = "rw" } };
        Assert.IsType<NotFoundObjectResult>(await CreateController().SavePermissions("missing", request, CancellationToken.None));
    }

    [Fact]
    public async Task UpdatePermissions_NonControlPlaneKey_ReturnsBadRequest()
    {
        var store = new StubApiKeyStore();
        var key = await store.CreateAsync("agent", ApiKeyScope.Agent);
        var request = new ApiKeyPermissionsDto { Permissions = new() { ["users"] = "rw" } };

        Assert.IsType<BadRequestObjectResult>(await CreateController(store).SavePermissions(key.Id, request, CancellationToken.None));
    }

    private sealed class StubApiKeyStore : IApiKeyStore
    {
        private readonly Dictionary<string, ApiKeyItem> _keys = new();
        public Dictionary<string, Dictionary<string, string>> Permissions { get; } = new();

        public Task<CreateApiKeyResponse> CreateAsync(string name, ApiKeyScope scope = ApiKeyScope.Inference, string? explicitKey = null, string? boundAgentName = null, string? permissionsJson = null, CancellationToken ct = default)
        {
            var id = Guid.NewGuid().ToString("N");
            var response = new CreateApiKeyResponse { Id = id, Name = name, Scope = scope, Secret = explicitKey ?? "secret-" + id, KeyPrefix = "secret", IsActive = true };
            _keys[id] = response;
            Permissions[id] = string.IsNullOrEmpty(permissionsJson) ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(permissionsJson)!;
            return Task.FromResult(response);
        }
        public Task<ApiKeyItem?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(_keys.TryGetValue(id, out var k) ? k : null);
        public Task<Dictionary<string, string>?> GetPermissionsAsync(string id, CancellationToken ct = default) => Task.FromResult(_keys.ContainsKey(id) ? Permissions[id] : null);
        public Task<Dictionary<string, string>?> SavePermissionsAsync(string id, string json, CancellationToken ct = default)
        {
            if (!_keys.ContainsKey(id)) return Task.FromResult<Dictionary<string, string>?>(null);
            Permissions[id] = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
            return Task.FromResult<Dictionary<string, string>?>(Permissions[id]);
        }
        public Task<IReadOnlyList<ApiKeyItem>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ApiKeyItem>>(_keys.Values.ToList());
        public Task<bool> RevokeAsync(string id, CancellationToken ct = default) => Task.FromResult(_keys.Remove(id));
        public Task<CreateApiKeyResponse> RotateAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ApiKeyEntity?> AuthenticateAsync(string secret, CancellationToken ct = default) => Task.FromResult<ApiKeyEntity?>(null);
        public Task<bool> HasAnyAsync(ApiKeyScope scope, CancellationToken ct = default) => Task.FromResult(_keys.Values.Any(k => k.Scope == scope));
        public Task UpdateLastUsedAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<AgentKeyBindingResult> ResolveAgentBindingAsync(string keyId, string claimedAgentName, CancellationToken ct = default) => Task.FromResult(AgentKeyBindingResult.Mismatch);
        public Task<KeyAccess?> GetAccessAsync(string keyId, CancellationToken ct = default) => Task.FromResult<KeyAccess?>(null);
        public Task<KeyAccess?> GetAccessCachedAsync(string keyId, CancellationToken ct = default) => Task.FromResult<KeyAccess?>(null);
        public Task<KeyAccess?> SaveAccessAsync(string keyId, KeyAccess access, CancellationToken ct = default) => Task.FromResult<KeyAccess?>(null);
        public Task<int> RemoveProviderFromAllKeysAsync(string providerName, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class StubCloudProviders : ICloudProviderStore
    {
        public Task CreateAsync(string n, string u, string p, string h, CancellationToken c = default) => Task.CompletedTask;
        public Task CreateAsync(string n, string u, string? p, string h, int a, CancellationToken c = default) => Task.CompletedTask;
        public Task UpdateAsync(string i, string u, string? p, string? h, CancellationToken c = default) => Task.CompletedTask;
        public Task<IReadOnlyList<CloudProviderListItem>> ListAsync(CancellationToken c = default) => Task.FromResult<IReadOnlyList<CloudProviderListItem>>([]);
        public Task<CloudProviderReadItem?> GetAsync(string i, CancellationToken c = default) => Task.FromResult<CloudProviderReadItem?>(null);
        public Task<bool> DeleteAsync(string i, CancellationToken c = default) => Task.FromResult(false);
        public Task<string?> GetApiKeyAsync(string i, CancellationToken c = default) => Task.FromResult<string?>(null);
        public Task SaveModelsAsync(string i, IReadOnlyList<string> m, CancellationToken c = default) => Task.CompletedTask;
        public Task<CloudProviderReadItem?> GetByNameAsync(string n, CancellationToken c = default) => Task.FromResult<CloudProviderReadItem?>(null);
        public Task<bool> NameExistsAsync(string n, CancellationToken c = default) => Task.FromResult(false);
        public Task<IReadOnlyList<string>> GetModelIdsAsync(string i, CancellationToken c = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task SaveModelsAsync(string i, IReadOnlyList<CloudProviderModelMeta> m, CancellationToken c = default) => Task.CompletedTask;
        public Task<IReadOnlyList<CloudProviderModelMeta>> GetModelMetasAsync(string i, CancellationToken c = default) => Task.FromResult<IReadOnlyList<CloudProviderModelMeta>>([]);
        public Task SaveOAuthTokensAsync(string i, string a, string r, DateTimeOffset? e, string? c, CancellationToken ct = default) => Task.CompletedTask;
        public Task<OAuthTokenSet?> GetOAuthTokensAsync(string i, CancellationToken c = default) => Task.FromResult<OAuthTokenSet?>(null);
        public Task<int> GetAuthTypeAsync(string i, CancellationToken c = default) => Task.FromResult(0);
    }

    private sealed class StubContainers : IContainerRegistry
    {
        public Task<IReadOnlyList<RegisteredRuntime>> ListAllAsync(CancellationToken c = default) => Task.FromResult<IReadOnlyList<RegisteredRuntime>>([]);
        public Task<RegisteredRuntime?> GetAsync(string i, CancellationToken c = default) => Task.FromResult<RegisteredRuntime?>(null);
        public Task<RegisteredRuntime> CreateAsync(RegisteredRuntime x, CancellationToken c = default) => Task.FromResult(x);
        public Task<RegisteredRuntime> UpdateAsync(string i, RegisteredRuntime x, CancellationToken c = default) => Task.FromResult(x);
        public Task DeleteAsync(string i, CancellationToken c = default) => Task.CompletedTask;
        public Task AddModelMappingAsync(string i, string m, CancellationToken c = default) => Task.CompletedTask;
        public Task RemoveModelMappingAsync(string i, string m, CancellationToken c = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetModelIdsForContainerAsync(string i, CancellationToken c = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> GetContainerIdForModelAsync(string m, CancellationToken c = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetAllContainerIdsForModelAsync(string m) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<(RegisteredRuntime A, RegisteredRuntime B)?> UpdateConcurrencyPairAsync(string a, IReadOnlyList<string> x, string b, IReadOnlyList<string> y, CancellationToken c = default) => Task.FromResult<(RegisteredRuntime A, RegisteredRuntime B)?>(null);
    }

    private sealed class StubRouterProfiles : IRouterProfileStore
    {
        public Task<IReadOnlyList<RouterProfile>> ListAsync(CancellationToken c = default) => Task.FromResult<IReadOnlyList<RouterProfile>>([]);
        public Task<RouterProfile?> GetAsync(string i, CancellationToken c = default) => Task.FromResult<RouterProfile?>(null);
        public Task<RouterProfile?> GetByNameAsync(string n, CancellationToken c = default) => Task.FromResult<RouterProfile?>(null);
        public Task<RouterProfile> CreateAsync(RouterProfile p, CancellationToken c = default) => Task.FromResult(p);
        public Task<RouterProfile> UpdateAsync(string i, RouterProfile p, CancellationToken c = default) => Task.FromResult(p);
        public Task SetActiveModelIdAsync(string i, string? m, CancellationToken c = default) => Task.CompletedTask;
        public Task<RouterProfile> SetThinkingEffortAsync(string i, string m, string? e, CancellationToken c = default) => Task.FromResult(new RouterProfile { Id = i, Name = "stub" });
        public Task DeleteAsync(string i, CancellationToken c = default) => Task.CompletedTask;
    }
}
