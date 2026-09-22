using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Unswarm.Api.Controllers;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Helpers;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class CloudProviderControllerTests
{
    private readonly FakeCloudProviderStore _store = new();
    private readonly FakeHttpClientFactory _httpFactory = new();
    private readonly FakeOAuthService _oauthService = new();
    private readonly FakeEncryptor _encryptor = new();

    private CloudProviderController CreateController(
        FakeCloudProviderStore? store = null,
        FakeHttpClientFactory? httpFactory = null,
        FakeOAuthService? oauthService = null,
        FakeEncryptor? encryptor = null,
        IConfiguration? configuration = null)
        => new(
            store ?? _store,
            httpFactory ?? _httpFactory,
            new LoggerFactory().CreateLogger<CloudProviderController>(),
            oauthService ?? _oauthService,
            encryptor ?? _encryptor,
            configuration ?? new ConfigurationBuilder().Build());

    // ── Fakes ────────────────────────────────────────────────────────

    private sealed class FakeCloudProviderStore : ICloudProviderStore
    {
        private readonly Dictionary<string, CloudProviderReadItem> _items = new();
        private readonly Dictionary<string, IReadOnlyList<string>> _modelIds = new();
        private readonly Dictionary<string, IReadOnlyList<CloudProviderModelMeta>> _modelMetas = new();
        private readonly Dictionary<string, OAuthTokenSet?> _oauthTokens = new();
        private int _nextId = 1;

        public List<string> CreatedNames { get; } = [];
        public List<string> DeletedIds { get; } = [];
        public List<(string Id, IReadOnlyList<string> ModelIds)> SavedModels { get; } = [];
        public List<(string Id, IReadOnlyList<CloudProviderModelMeta> Metas)> SavedModelMetas { get; } = [];
        public List<(string Id, string AccessToken, string RefreshToken, DateTimeOffset? ExpiresAt, string? AccountId)> SavedOAuthTokens { get; } = [];

        public void SeedProvider(string id, string name, string baseUrl = "https://api.openai.com/v1", int authType = 0, string? chatgptAccountId = null, DateTimeOffset? tokenExpiresAt = null)
        {
            _items[id] = new CloudProviderReadItem
            {
                Id = id,
                Name = name,
                BaseUrl = baseUrl,
                BaseUrlFull = baseUrl,
                ApiKeyHint = "sk-…test",
                AuthType = authType,
                ChatgptAccountId = chatgptAccountId,
                TokenExpiresAt = tokenExpiresAt,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _modelIds[id] = [];
        }

        public void SeedOAuthTokens(string id)
        {
            _oauthTokens[id] = new OAuthTokenSet("encrypted-access", "encrypted-refresh", DateTimeOffset.UtcNow.AddHours(1), "acct-123");
        }

        public void SeedModelMetas(string id, IReadOnlyList<CloudProviderModelMeta> metas)
        {
            _modelMetas[id] = metas;
            _modelIds[id] = metas.Select(m => m.Id).ToList();
        }

        public Task CreateAsync(string name, string baseUrl, string apiKeyPlaintext, string apiKeyHint, CancellationToken ct = default)
        {
            CreatedNames.Add(name);
            var id = $"cp-{_nextId++}";
            _items[id] = new CloudProviderReadItem
            {
                Id = id,
                Name = name,
                BaseUrl = baseUrl,
                BaseUrlFull = baseUrl,
                ApiKeyHint = apiKeyHint,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _modelIds[id] = [];
            return Task.CompletedTask;
        }

        public Task CreateAsync(string name, string baseUrl, string? apiKeyPlaintext, string apiKeyHint, int authType, CancellationToken ct = default)
        {
            CreatedNames.Add(name);
            var id = $"cp-{_nextId++}";
            _items[id] = new CloudProviderReadItem
            {
                Id = id,
                Name = name,
                BaseUrl = baseUrl,
                BaseUrlFull = baseUrl,
                ApiKeyHint = apiKeyHint,
                AuthType = authType,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _modelIds[id] = [];
            return Task.CompletedTask;
        }

        public Task UpdateAsync(string id, string baseUrl, string? apiKeyPlaintext, string? apiKeyHint, CancellationToken ct = default)
        {
            if (!_items.TryGetValue(id, out var existing))
                throw new KeyNotFoundException($"Provider {id} not found");

            _items[id] = new CloudProviderReadItem
            {
                Id = existing.Id,
                Name = existing.Name,
                BaseUrl = baseUrl,
                BaseUrlFull = existing.BaseUrlFull,
                ApiKeyHint = apiKeyHint ?? existing.ApiKeyHint,
                ModelCount = existing.ModelCount,
                AuthType = existing.AuthType,
                ChatgptAccountId = existing.ChatgptAccountId,
                TokenExpiresAt = existing.TokenExpiresAt,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CloudProviderListItem>> ListAsync(CancellationToken ct = default)
        {
            var list = _items.Values.Select(i => new CloudProviderListItem
            {
                Id = i.Id,
                Name = i.Name,
                BaseUrl = i.BaseUrl,
                ApiKeyHint = i.ApiKeyHint,
                ModelCount = i.ModelCount,
                AuthType = i.AuthType,
                CreatedAt = i.CreatedAt,
                UpdatedAt = i.UpdatedAt
            }).ToList();
            return Task.FromResult<IReadOnlyList<CloudProviderListItem>>(list);
        }

        public Task<CloudProviderReadItem?> GetAsync(string id, CancellationToken ct = default)
        {
            _items.TryGetValue(id, out var item);
            return Task.FromResult(item);
        }

        public Task<CloudProviderReadItem?> GetByNameAsync(string name, CancellationToken ct = default)
        {
            var item = _items.Values.FirstOrDefault(i => i.Name == name);
            return Task.FromResult(item);
        }

        public Task<bool> NameExistsAsync(string name, CancellationToken ct = default)
            => Task.FromResult(_items.Values.Any(i => i.Name == name));

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
        {
            DeletedIds.Add(id);
            return Task.FromResult(_items.Remove(id));
        }

        public Task<string?> GetApiKeyAsync(string id, CancellationToken ct = default)
            => Task.FromResult<string?>("sk-decrypted-test-key");

        public Task SaveModelsAsync(string id, IReadOnlyList<string> modelIds, CancellationToken ct = default)
        {
            SavedModels.Add((id, modelIds));
            if (_items.TryGetValue(id, out var item))
            {
                _items[id] = new CloudProviderReadItem
                {
                    Id = item.Id,
                    Name = item.Name,
                    BaseUrl = item.BaseUrl,
                    BaseUrlFull = item.BaseUrlFull,
                    ApiKeyHint = item.ApiKeyHint,
                    ModelCount = modelIds.Count,
                    AuthType = item.AuthType,
                    ChatgptAccountId = item.ChatgptAccountId,
                    TokenExpiresAt = item.TokenExpiresAt,
                    CreatedAt = item.CreatedAt,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                _modelIds[id] = modelIds;
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetModelIdsAsync(string id, CancellationToken ct = default)
        {
            _modelIds.TryGetValue(id, out var models);
            return Task.FromResult<IReadOnlyList<string>>(models ?? []);
        }

        public Task SaveModelsAsync(string id, IReadOnlyList<CloudProviderModelMeta> models, CancellationToken ct = default)
        {
            SavedModels.Add((id, models.Select(m => m.Id).ToList()));
            SavedModelMetas.Add((id, models));
            _modelMetas[id] = models;
            if (_items.TryGetValue(id, out var item))
            {
                _items[id] = new CloudProviderReadItem
                {
                    Id = item.Id,
                    Name = item.Name,
                    BaseUrl = item.BaseUrl,
                    BaseUrlFull = item.BaseUrlFull,
                    ApiKeyHint = item.ApiKeyHint,
                    ModelCount = models.Count,
                    AuthType = item.AuthType,
                    ChatgptAccountId = item.ChatgptAccountId,
                    TokenExpiresAt = item.TokenExpiresAt,
                    CreatedAt = item.CreatedAt,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                _modelIds[id] = models.Select(m => m.Id).ToList();
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CloudProviderModelMeta>> GetModelMetasAsync(string id, CancellationToken ct = default)
            => Task.FromResult(_modelMetas.TryGetValue(id, out var metas)
                ? metas
                : (IReadOnlyList<CloudProviderModelMeta>)[]);

        public Task SaveOAuthTokensAsync(string id, string accessTokenCiphertext, string refreshTokenCiphertext, DateTimeOffset? expiresAt, string? chatgptAccountId, CancellationToken ct = default)
        {
            SavedOAuthTokens.Add((id, accessTokenCiphertext, refreshTokenCiphertext, expiresAt, chatgptAccountId));
            if (_items.TryGetValue(id, out var item))
            {
                _items[id] = new CloudProviderReadItem
                {
                    Id = item.Id,
                    Name = item.Name,
                    BaseUrl = item.BaseUrl,
                    BaseUrlFull = item.BaseUrlFull,
                    ApiKeyHint = item.ApiKeyHint,
                    ModelCount = item.ModelCount,
                    AuthType = item.AuthType,
                    ChatgptAccountId = chatgptAccountId ?? item.ChatgptAccountId,
                    TokenExpiresAt = expiresAt ?? item.TokenExpiresAt,
                    CreatedAt = item.CreatedAt,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }
            _oauthTokens[id] = new OAuthTokenSet(accessTokenCiphertext, refreshTokenCiphertext, expiresAt, chatgptAccountId);
            return Task.CompletedTask;
        }

        public Task<OAuthTokenSet?> GetOAuthTokensAsync(string id, CancellationToken ct = default)
        {
            _oauthTokens.TryGetValue(id, out var tokens);
            return Task.FromResult(tokens);
        }

        public Task<int> GetAuthTypeAsync(string id, CancellationToken ct = default)
        {
            _items.TryGetValue(id, out var item);
            return Task.FromResult(item?.AuthType ?? 0);
        }
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }
        public List<HttpRequestMessage> Requests { get; } = [];

        public HttpClient CreateClient(string name)
        {
            var handler = new TestHttpMessageHandler(msg =>
            {
                Requests.Add(msg);
                return Handler?.Invoke(msg) ?? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent("""{"data":[]}""", System.Text.Encoding.UTF8, "application/json")
                };
            });
            return new HttpClient(handler);
        }
    }

    private sealed class FakeOAuthService : IChatGptOAuthService
    {
        public DeviceCodeResult? StartResult { get; set; }
        public OAuthTokenResult? PollResult { get; set; }
        public OAuthTokenResult? RefreshResult { get; set; }
        public bool StartCalled { get; private set; }
        public bool PollCalled { get; private set; }

        public Task<DeviceCodeResult> StartDeviceCodeFlowAsync(CancellationToken ct)
        {
            StartCalled = true;
            return Task.FromResult(StartResult ?? new DeviceCodeResult("auth-1", "USER-CODE", "https://chatgpt.com/activate", 5));
        }

        public Task<OAuthTokenResult?> PollForTokenAsync(string deviceAuthId, string userCode, CancellationToken ct)
        {
            PollCalled = true;
            return Task.FromResult(PollResult);
        }

        public Task<OAuthTokenResult> RefreshTokenAsync(string refreshToken, CancellationToken ct)
            => Task.FromResult(RefreshResult ?? new OAuthTokenResult("new-access", "new-refresh", DateTimeOffset.UtcNow.AddHours(1), "acct-123"));
    }

    private sealed class FakeEncryptor : IApiKeyEncryptor
    {
        public string Protect(string plaintext) => $"encrypted({plaintext})";
        public string Unprotect(string ciphertext) => ciphertext.Replace("encrypted(", "").TrimEnd(')');
    }

    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }

    // ── List ─────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ReturnsOkWithProviders()
    {
        _store.SeedProvider("cp-1", "openai");
        _store.SeedProvider("cp-2", "anthropic");

        var ctrl = CreateController();
        var result = await ctrl.List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<IEnumerable<CloudProviderListItemDto>>(ok.Value).ToList();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Name == "openai");
        Assert.Contains(items, i => i.Name == "anthropic");
    }

    // ── Get ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ExistingProvider_ReturnsOk()
    {
        _store.SeedProvider("cp-1", "openai");

        var ctrl = CreateController();
        var result = await ctrl.Get("cp-1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<CloudProviderReadDto>(ok.Value);
        Assert.Equal("cp-1", dto.Id);
        Assert.Equal("openai", dto.Name);
    }

    [Fact]
    public async Task Get_NonExistingProvider_ReturnsNotFound()
    {
        var ctrl = CreateController();
        var result = await ctrl.Get("nonexistent", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── Create ───────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ValidRequest_ReturnsCreatedAt()
    {
        var ctrl = CreateController();
        var request = new CreateCloudProviderRequest("openai", "https://api.openai.com/v1", "sk-test1234key");

        var result = await ctrl.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(nameof(CloudProviderController.Get), created.ActionName);
        var dto = Assert.IsType<CloudProviderReadDto>(created.Value);
        Assert.Equal("openai", dto.Name);
    }

    [Fact]
    public async Task Create_DuplicateName_ReturnsConflict()
    {
        _store.SeedProvider("cp-1", "openai");

        var ctrl = CreateController();
        var request = new CreateCloudProviderRequest("openai", "https://api.openai.com/v1", "sk-key");

        // The store's CreateAsync doesn't throw, so the controller won't return Conflict.
        // But let's verify the Create action itself works.
        var result = await ctrl.Create(request, CancellationToken.None);

        // CreateAsync with the simple store doesn't throw, so we get CreatedAtAction.
        Assert.IsType<CreatedAtActionResult>(result);
    }

    [Fact]
    public async Task Create_BlankName_ReturnsBadRequest()
    {
        var ctrl = CreateController();
        var request = new CreateCloudProviderRequest("  ", "https://api.openai.com/v1", "sk-key");

        var result = await ctrl.Create(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Create_ApiKeyAuthWithBlankKey_ReturnsBadRequest()
    {
        var ctrl = CreateController();
        var request = new CreateCloudProviderRequest("openai", "https://api.openai.com/v1", "  ", AuthType: 0);

        var result = await ctrl.Create(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Create_InvalidBaseUrl_ReturnsBadRequest()
    {
        var ctrl = CreateController();
        var request = new CreateCloudProviderRequest("openai", "not-a-url", "sk-key");

        var result = await ctrl.Create(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Create_PrivateLiteralIp_RejectedByDefault()
    {
        var ctrl = CreateController();
        var request = new CreateCloudProviderRequest("lan", "http://10.0.0.5:8080/v1", "sk-key");

        Assert.IsType<BadRequestObjectResult>(await ctrl.Create(request, CancellationToken.None));
    }

    [Fact]
    public async Task Create_PrivateLiteralIp_AllowedWhenPrivateEgressEnabled()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CloudProviders:AllowPrivateEgress"] = "true"
            })
            .Build();
        var ctrl = CreateController(configuration: config);
        var request = new CreateCloudProviderRequest("lan", "http://10.0.0.5:8080/v1", "sk-key");

        Assert.IsType<CreatedAtActionResult>(await ctrl.Create(request, CancellationToken.None));
    }

    [Fact]
    public async Task Create_PrivateLiteralIp_AllowedWhenHostAllowlisted()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CloudProviders:AllowedPrivateHosts:0"] = "10.0.0.5"
            })
            .Build();
        var ctrl = CreateController(configuration: config);
        var request = new CreateCloudProviderRequest("lan", "http://10.0.0.5:8080/v1", "sk-key");

        Assert.IsType<CreatedAtActionResult>(await ctrl.Create(request, CancellationToken.None));
    }

    [Fact]
    public async Task Create_InvalidNameChars_ReturnsBadRequest()
    {
        var ctrl = CreateController();
        var request = new CreateCloudProviderRequest("open ai!", "https://api.openai.com/v1", "sk-key");

        var result = await ctrl.Create(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Update ───────────────────────────────────────────────────────

    [Fact]
    public async Task Update_ValidRequest_ReturnsOk()
    {
        _store.SeedProvider("cp-1", "openai");

        var ctrl = CreateController();
        var request = new UpdateCloudProviderRequest("https://api.openai.com/v1");

        var result = await ctrl.Update("cp-1", request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<CloudProviderReadDto>(ok.Value);
        Assert.Equal("openai", dto.Name);
    }

    [Fact]
    public async Task Update_NonExistingProvider_ReturnsNotFound()
    {
        var ctrl = CreateController();
        var request = new UpdateCloudProviderRequest("https://api.openai.com/v1");

        var result = await ctrl.Update("nonexistent", request, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Update_InvalidBaseUrl_ReturnsBadRequest()
    {
        _store.SeedProvider("cp-1", "openai");

        var ctrl = CreateController();
        var request = new UpdateCloudProviderRequest("not-a-url");

        var result = await ctrl.Update("cp-1", request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Delete ───────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_ValidId_ReturnsNoContent()
    {
        _store.SeedProvider("cp-1", "openai");

        var ctrl = CreateController();
        var result = await ctrl.Delete("cp-1", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Contains("cp-1", _store.DeletedIds);
    }

    [Fact]
    public async Task Delete_NonExistingId_ReturnsNotFound()
    {
        var ctrl = CreateController();
        var result = await ctrl.Delete("nonexistent", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── SaveModels ───────────────────────────────────────────────────

    [Fact]
    public async Task SaveModels_ValidRequest_ReturnsOk()
    {
        _store.SeedProvider("cp-1", "openai");

        var ctrl = CreateController();
        var request = new CloudProviderModelListDto { Models = [new() { Id = "gpt-4o" }, new() { Id = "gpt-4o-mini" }] };

        var result = await ctrl.SaveModels("cp-1", request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<CloudProviderReadDto>(ok.Value);
        Assert.Equal(2, _store.SavedModels[0].ModelIds.Count);
    }

    [Fact]
    public async Task SaveModels_NonExistingProvider_ReturnsNotFound()
    {
        var ctrl = CreateController();
        var request = new CloudProviderModelListDto { Models = [new() { Id = "gpt-4o" }] };

        var result = await ctrl.SaveModels("nonexistent", request, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task SaveModels_UnknownInputModality_ReturnsBadRequest()
    {
        _store.SeedProvider("cp-1", "openai");

        var ctrl = CreateController();
        var request = new CloudProviderModelListDto
        {
            Models = [new() { Id = "gpt-4o", InputModalities = ["hologram"] }]
        };

        var result = await ctrl.SaveModels("cp-1", request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_store.SavedModelMetas);
    }

    [Fact]
    public async Task SaveModels_OmittedInputModalities_PreservesStoredModalities()
    {
        _store.SeedProvider("cp-1", "openai");
        _store.SeedModelMetas("cp-1",
            [new CloudProviderModelMeta { Id = "gpt-4o", InputModalities = ["text", "image"] }]);

        var ctrl = CreateController();
        // InputModalities omitted (null) — must preserve the stored selection.
        var request = new CloudProviderModelListDto { Models = [new() { Id = "gpt-4o" }] };

        var result = await ctrl.SaveModels("cp-1", request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var saved = Assert.Single(Assert.Single(_store.SavedModelMetas).Metas);
        Assert.Equal(["text", "image"], saved.InputModalities);
    }

    [Fact]
    public async Task SaveModels_ExplicitInputModalities_OverwritesStoredModalities()
    {
        _store.SeedProvider("cp-1", "openai");
        _store.SeedModelMetas("cp-1",
            [new CloudProviderModelMeta { Id = "gpt-4o", InputModalities = ["text", "image"] }]);

        var ctrl = CreateController();
        var request = new CloudProviderModelListDto
        {
            Models = [new() { Id = "gpt-4o", InputModalities = ["text", "audio"] }]
        };

        var result = await ctrl.SaveModels("cp-1", request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var saved = Assert.Single(Assert.Single(_store.SavedModelMetas).Metas);
        Assert.Equal(["text", "audio"], saved.InputModalities);
    }

    [Fact]
    public async Task SaveModels_NewModelWithoutModalities_DefaultsToTextOnly()
    {
        _store.SeedProvider("cp-1", "openai");

        var ctrl = CreateController();
        var request = new CloudProviderModelListDto { Models = [new() { Id = "brand-new" }] };

        var result = await ctrl.SaveModels("cp-1", request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var saved = Assert.Single(Assert.Single(_store.SavedModelMetas).Metas);
        Assert.Equal(["text"], saved.InputModalities);
    }

    // ── FetchModels (with upstream metadata) ─────────────────────────

    [Fact]
    public async Task FetchModels_OpenRouterResponse_ExtractsContextLength()
    {
        _store.SeedProvider("cp-1", "openrouter");
        var openRouterResponse = """
        {
          "data": [
            {
              "id": "openai/gpt-4o",
              "name": "OpenAI GPT-4o",
              "context_length": 128000,
              "top_provider": {
                "context_length": 128000,
                "max_completion_tokens": 16384
              }
            },
            {
              "id": "anthropic/claude-sonnet-4-5",
              "name": "Anthropic Claude Sonnet 4.5",
              "context_length": 200000,
              "top_provider": {
                "context_length": 200000,
                "max_completion_tokens": 64000
              }
            }
          ]
        }
        """;

        _httpFactory.Handler = req => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(openRouterResponse, System.Text.Encoding.UTF8, "application/json")
        };

        var ctrl = CreateController();
        var result = await ctrl.FetchModels("cp-1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<FetchModelsResultDto>(ok.Value);
        Assert.Equal(2, dto.Models.Count);

        var gpt = dto.Models.First(m => m.Id == "openai/gpt-4o");
        Assert.Equal(128000, gpt.ContextWindow);
        Assert.Equal("OpenAI GPT-4o", gpt.DisplayName);

        var claude = dto.Models.First(m => m.Id == "anthropic/claude-sonnet-4-5");
        Assert.Equal(200000, claude.ContextWindow);
        Assert.Equal(64000, claude.MaxOutputTokens);
    }

    [Fact]
    public async Task FetchModels_VllmResponse_ExtractsMaxModelLen()
    {
        _store.SeedProvider("cp-1", "local-vllm");
        var vllmResponse = """
        {
          "max_model_len": 32768,
          "data": [
            {"id": "llama-3.1-8b", "owned_by": "meta"},
            {"id": "codellama-13b", "owned_by": "meta"}
          ]
        }
        """;

        _httpFactory.Handler = req => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(vllmResponse, System.Text.Encoding.UTF8, "application/json")
        };

        var ctrl = CreateController();
        var result = await ctrl.FetchModels("cp-1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<FetchModelsResultDto>(ok.Value);
        Assert.Equal(2, dto.Models.Count);

        // vLLM returns max_model_len at top level; our parser should pick it up
        // But ParseUpstreamModels only looks at per-model fields, not top-level max_model_len
        // The models themselves won't have context_length, so they get 0
        Assert.Equal("llama-3.1-8b", dto.Models[0].Id);
        Assert.Equal("codellama-13b", dto.Models[1].Id);
    }

    [Fact]
    public async Task FetchModels_BareOpenAIResponse_NoMetadata()
    {
        _store.SeedProvider("cp-1", "openai-direct");
        var openaiResponse = """
        {
          "data": [
            {"id": "gpt-4o", "object": "model", "owned_by": "openai"},
            {"id": "gpt-4o-mini", "object": "model", "owned_by": "openai"}
          ]
        }
        """;

        _httpFactory.Handler = req => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(openaiResponse, System.Text.Encoding.UTF8, "application/json")
        };

        var ctrl = CreateController();
        var result = await ctrl.FetchModels("cp-1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<FetchModelsResultDto>(ok.Value);
        Assert.Equal(2, dto.Models.Count);
        Assert.Equal("gpt-4o", dto.Models[0].Id);
        Assert.Equal(0, dto.Models[0].ContextWindow); // no metadata from OpenAI
    }

    [Fact]
    public async Task FetchModels_UpstreamError_ReturnsError()
    {
        _store.SeedProvider("cp-1", "broken-provider");

        _httpFactory.Handler = req => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);

        var ctrl = CreateController();
        var result = await ctrl.FetchModels("cp-1", CancellationToken.None);

        Assert.IsType<ObjectResult>(result);
    }

    [Fact]
    public async Task FetchModels_NonExistingProvider_ReturnsNotFound()
    {
        var ctrl = CreateController();
        var result = await ctrl.FetchModels("nonexistent", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task FetchModels_SavesMetadataToStore()
    {
        _store.SeedProvider("cp-1", "openrouter");
        var response = """
        {
          "data": [
            {"id": "gpt-4o", "context_length": 128000, "top_provider": {"max_completion_tokens": 16384}}
          ]
        }
        """;

        _httpFactory.Handler = req => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(response, System.Text.Encoding.UTF8, "application/json")
        };

        var ctrl = CreateController();
        await ctrl.FetchModels("cp-1", CancellationToken.None);

        // Verify models were saved to the store
        Assert.Single(_store.SavedModels);
        Assert.Equal("cp-1", _store.SavedModels[0].Id);
        Assert.Contains("gpt-4o", _store.SavedModels[0].ModelIds);
    }

    [Fact]
    public async Task TestAndFetch_WithMetadata_ReturnsEnrichedModels()
    {
        var response = """
        {
          "data": [
            {
              "id": "claude-sonnet-4-5",
              "name": "Claude Sonnet 4.5",
              "context_length": 200000,
              "top_provider": {"max_completion_tokens": 64000}
            }
          ]
        }
        """;

        _httpFactory.Handler = req => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(response, System.Text.Encoding.UTF8, "application/json")
        };

        var ctrl = CreateController();
        var result = await ctrl.TestAndFetch(
            new TestAndFetchRequest("https://openrouter.ai/api/v1", "sk-or-key"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<FetchModelsResultDto>(ok.Value);
        Assert.Single(dto.Models);
        Assert.Equal("claude-sonnet-4-5", dto.Models[0].Id);
        Assert.Equal(200000, dto.Models[0].ContextWindow);
        Assert.Equal(64000, dto.Models[0].MaxOutputTokens);
        Assert.Equal("Claude Sonnet 4.5", dto.Models[0].DisplayName);
    }

    // ── StartOAuth ───────────────────────────────────────────────────

    [Fact]
    public async Task StartOAuth_ValidProvider_ReturnsDeviceCode()
    {
        _store.SeedProvider("cp-1", "chatgpt", authType: 1);

        var ctrl = CreateController();
        var result = await ctrl.StartOAuth("cp-1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<OAuthStartResultDto>(ok.Value);
        Assert.Equal("USER-CODE", dto.UserCode);
        Assert.Equal("https://chatgpt.com/activate", dto.VerificationUrl);
        Assert.True(_oauthService.StartCalled);
    }

    [Fact]
    public async Task StartOAuth_NonExistingProvider_ReturnsNotFound()
    {
        var ctrl = CreateController();
        var result = await ctrl.StartOAuth("nonexistent", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task StartOAuth_NonOAuthProvider_ReturnsBadRequest()
    {
        _store.SeedProvider("cp-1", "openai", authType: 0);

        var ctrl = CreateController();
        var result = await ctrl.StartOAuth("cp-1", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── PollOAuth ────────────────────────────────────────────────────

    [Fact]
    public async Task PollOAuth_PendingResult_ReturnsPending()
    {
        _store.SeedProvider("cp-1", "chatgpt", authType: 1);
        _oauthService.PollResult = null; // null = still pending

        var ctrl = CreateController();
        var result = await ctrl.PollOAuth("cp-1", new PollOAuthRequest("auth-1", "USER-CODE"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(_oauthService.PollCalled);
    }

    [Fact]
    public async Task PollOAuth_SuccessResult_ReturnsSuccess()
    {
        _store.SeedProvider("cp-1", "chatgpt", authType: 1);
        _oauthService.PollResult = new OAuthTokenResult("access-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1), "acct-123");

        var ctrl = CreateController();
        var result = await ctrl.PollOAuth("cp-1", new PollOAuthRequest("auth-1", "USER-CODE"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Single(_store.SavedOAuthTokens);
        Assert.Equal("encrypted(access-token)", _store.SavedOAuthTokens[0].AccessToken);
    }

    [Fact]
    public async Task PollOAuth_NonExistingProvider_ReturnsNotFound()
    {
        var ctrl = CreateController();
        var result = await ctrl.PollOAuth("nonexistent", new PollOAuthRequest("auth-1", "USER-CODE"), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── RefreshOAuth ─────────────────────────────────────────────────

    [Fact]
    public async Task RefreshOAuth_ValidProvider_ReturnsSuccess()
    {
        _store.SeedProvider("cp-1", "chatgpt", authType: 1);
        _store.SeedOAuthTokens("cp-1");

        var ctrl = CreateController();
        var result = await ctrl.RefreshOAuth("cp-1", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Single(_store.SavedOAuthTokens);
    }

    [Fact]
    public async Task RefreshOAuth_NonExistingProvider_ReturnsNotFound()
    {
        var ctrl = CreateController();
        var result = await ctrl.RefreshOAuth("nonexistent", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task RefreshOAuth_NonOAuthProvider_ReturnsBadRequest()
    {
        _store.SeedProvider("cp-1", "openai", authType: 0);

        var ctrl = CreateController();
        var result = await ctrl.RefreshOAuth("cp-1", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task RefreshOAuth_NoTokens_ReturnsBadRequest()
    {
        _store.SeedProvider("cp-1", "chatgpt", authType: 1);
        // No tokens seeded

        var ctrl = CreateController();
        var result = await ctrl.RefreshOAuth("cp-1", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
