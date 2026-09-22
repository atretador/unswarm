using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Persistence;
using Unswarm.Core.Services;

namespace Unswarm.Tests.Unit;

public sealed class CloudProviderStoreTests
{
    private static ILogger<CloudProviderStore> Log() => new LoggerFactory().CreateLogger<CloudProviderStore>();

    private static IApiKeyEncryptor FakeEncryptor() => new FakeEncryptorImpl();

    private static (Func<UnswarmDbContext> factory, SqliteConnection connection) BuildDb()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var factory = () =>
        {
            var options = new DbContextOptionsBuilder<UnswarmDbContext>()
                .UseSqlite(connection)
                .Options;
            return new UnswarmDbContext(options);
        };
        using (var db = factory())
        {
            db.Database.EnsureCreated();
        }
        return (factory, connection);
    }

    private static async Task<string> CreateTestProviderAsync(
        CloudProviderStore store,
        string name = "openai",
        string baseUrl = "https://api.openai.com/v1",
        string apiKey = "sk-test123",
        string hint = "sk-…3f9a")
    {
        await store.CreateAsync(name, baseUrl, apiKey, hint);
        var item = await store.GetByNameAsync(name);
        Assert.NotNull(item);
        return item.Id;
    }

    // ── CreateAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_HappyPath_CreatesEntityWithEncryptedKey()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            await store.CreateAsync("openai", "https://api.openai.com/v1", "sk-test", "sk-…3f9a");

            var list = await store.ListAsync();
            Assert.Single(list);
            Assert.Equal("openai", list[0].Name);
            Assert.Equal("https://api.openai.com/v1", list[0].BaseUrl);
            Assert.Equal("sk-…3f9a", list[0].ApiKeyHint);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task CreateAsync_DuplicateName_ThrowsInvalidOperationException()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            await store.CreateAsync("openai", "https://api.openai.com/v1", "sk-test", "sk-…");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.CreateAsync("openai", "https://api.openai.com/v1", "sk-other", "sk-…"));
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task CreateAsync_NullApiKey_CreatesWithEmptyCiphertext()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            await store.CreateAsync("chatgpt", "https://chatgpt.com", null, "");

            var key = await store.GetApiKeyAsync((await store.GetByNameAsync("chatgpt"))!.Id);
            Assert.Equal("", key);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task CreateAsync_EmptyApiKey_CreatesWithEmptyCiphertext()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            await store.CreateAsync("chatgpt", "https://chatgpt.com", "", "");

            var id = (await store.GetByNameAsync("chatgpt"))!.Id;
            var key = await store.GetApiKeyAsync(id);
            Assert.Equal("", key);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task CreateAsync_WithAuthType_SetsAuthType()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            await store.CreateAsync("chatgpt", "https://chatgpt.com", null, "", authType: 1);

            var item = await store.GetByNameAsync("chatgpt");
            Assert.NotNull(item);
            Assert.Equal(1, item.AuthType);
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── UpdateAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_UpdatesBaseUrlAndApiKey()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store);

            await store.UpdateAsync(id, "https://api.openai.com/v2", "sk-newkey", "sk-…new");

            var item = await store.GetAsync(id);
            Assert.NotNull(item);
            Assert.Equal("https://api.openai.com/v2", item.BaseUrl);
            Assert.Equal("sk-…new", item.ApiKeyHint);
            var decryptedKey = await store.GetApiKeyAsync(id);
            Assert.Equal("sk-newkey", decryptedKey);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task UpdateAsync_MissingProvider_ThrowsKeyNotFoundException()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            await Assert.ThrowsAsync<KeyNotFoundException>(
                () => store.UpdateAsync("cp_nonexistent", "https://x.com", "key", "hint"));
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task UpdateAsync_NullApiKey_PreservesExistingKey()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store, apiKey: "sk-original");

            await store.UpdateAsync(id, "https://api.openai.com/v2", null, null);

            var key = await store.GetApiKeyAsync(id);
            Assert.Equal("sk-original", key);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task UpdateAsync_EmptyApiKey_PreservesExistingKey()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store, apiKey: "sk-original");

            await store.UpdateAsync(id, "https://api.openai.com/v2", "", null);

            var key = await store.GetApiKeyAsync(id);
            Assert.Equal("sk-original", key);
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── ListAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_ReturnsOrderedList()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            await CreateTestProviderAsync(store, name: "openai");
            await CreateTestProviderAsync(store, name: "anthropic");
            await CreateTestProviderAsync(store, name: "deepseek");

            var list = await store.ListAsync();
            Assert.Equal(3, list.Count);
            Assert.Equal("anthropic", list[0].Name);
            Assert.Equal("deepseek", list[1].Name);
            Assert.Equal("openai", list[2].Name);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task ListAsync_EmptyDatabase_ReturnsEmptyList()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var list = await store.ListAsync();
            Assert.Empty(list);
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── GetAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_ReturnsReadItemWithDecryptedHint()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store);

            var item = await store.GetAsync(id);
            Assert.NotNull(item);
            Assert.Equal("openai", item.Name);
            Assert.Equal("sk-…3f9a", item.ApiKeyHint);
            Assert.Equal("https://api.openai.com/v1", item.BaseUrlFull);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task GetAsync_Missing_ReturnsNull()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var item = await store.GetAsync("cp_nonexistent");
            Assert.Null(item);
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── DeleteAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_RemovesEntityAndReturnsTrue()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store);

            var result = await store.DeleteAsync(id);

            Assert.True(result);
            Assert.Null(await store.GetAsync(id));
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task DeleteAsync_Missing_ReturnsFalse()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var result = await store.DeleteAsync("cp_nonexistent");
            Assert.False(result);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task DeleteAsync_WithApiKeyStore_CleansUpAccessRecords()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var fakeKeyStore = new FakeApiKeyStore();
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log(), fakeKeyStore);
            var id = await CreateTestProviderAsync(store, name: "openai");

            await store.DeleteAsync(id);

            Assert.Equal(["openai"], fakeKeyStore.RemovedProviderNames);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task DeleteAsync_WithoutApiKeyStore_DoesNotThrow()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log(), apiKeyStore: null);
            var id = await CreateTestProviderAsync(store);

            var result = await store.DeleteAsync(id);
            Assert.True(result);
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── GetApiKeyAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetApiKeyAsync_ReturnsDecryptedKey()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store, apiKey: "sk-super-secret");

            var key = await store.GetApiKeyAsync(id);
            Assert.Equal("sk-super-secret", key);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task GetApiKeyAsync_MissingProvider_ReturnsNull()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var key = await store.GetApiKeyAsync("cp_nonexistent");
            Assert.Null(key);
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── SaveModelsAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task SaveModelsAsync_SavesModelIdsAsJson()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store);

            await store.SaveModelsAsync(id, ["gpt-4o", "gpt-4o-mini"]);

            var models = await store.GetModelIdsAsync(id);
            Assert.Equal(2, models.Count);
            Assert.Contains("gpt-4o", models);
            Assert.Contains("gpt-4o-mini", models);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task SaveModelsAsync_TooManyModels_ThrowsArgumentException()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store);
            var tooMany = Enumerable.Range(1, 501).Select(i => $"model-{i}").ToList();

            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveModelsAsync(id, tooMany));
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task SaveModelsAsync_CloudPrefix_ThrowsArgumentException()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store);

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.SaveModelsAsync(id, ["cloud/some-model"]));
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task SaveModelsAsync_EmptyModelId_ThrowsArgumentException()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store);

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.SaveModelsAsync(id, [""]));
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task SaveModelsAsync_MissingProvider_ThrowsKeyNotFoundException()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            await Assert.ThrowsAsync<KeyNotFoundException>(
                () => store.SaveModelsAsync("cp_nonexistent", ["gpt-4o"]));
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── GetByNameAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetByNameAsync_FindsByName()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            await CreateTestProviderAsync(store, name: "anthropic");

            var item = await store.GetByNameAsync("anthropic");
            Assert.NotNull(item);
            Assert.Equal("anthropic", item.Name);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task GetByNameAsync_Missing_ReturnsNull()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var item = await store.GetByNameAsync("nonexistent");
            Assert.Null(item);
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── NameExistsAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task NameExistsAsync_True_WhenExists()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            await CreateTestProviderAsync(store, name: "openai");

            Assert.True(await store.NameExistsAsync("openai"));
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task NameExistsAsync_False_WhenNotExists()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            Assert.False(await store.NameExistsAsync("nonexistent"));
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── GetModelIdsAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetModelIdsAsync_ParsesJsonArray()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store);
            await store.SaveModelsAsync(id, ["gpt-4o", "gpt-4o-mini", "o1-preview"]);

            var models = await store.GetModelIdsAsync(id);
            Assert.Equal(3, models.Count);
            Assert.Equal("gpt-4o", models[0]);
            Assert.Equal("gpt-4o-mini", models[1]);
            Assert.Equal("o1-preview", models[2]);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task GetModelIdsAsync_MissingProvider_ReturnsEmpty()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var models = await store.GetModelIdsAsync("cp_nonexistent");
            Assert.Empty(models);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task GetModelIdsAsync_DefaultModelsJson_ReturnsEmpty()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store);
            // New provider has ModelsJson = "[]"
            var models = await store.GetModelIdsAsync(id);
            Assert.Empty(models);
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── SaveModelsAsync (metadata overload) ──────────────────────────────

    [Fact]
    public async Task SaveModelsAsync_WithMetadata_SavesAndReadsBack()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store);

            var metas = new List<CloudProviderModelMeta>
            {
                new() { Id = "gpt-5.4", ContextWindow = 1050000, MaxOutputTokens = 128000, Family = "gpt", DisplayName = "GPT 5.4" },
                new() { Id = "claude-sonnet-4-5", ContextWindow = 200000, MaxOutputTokens = 64000, Family = "claude" }
            };
            await store.SaveModelsAsync(id, metas);

            var result = await store.GetModelMetasAsync(id);
            Assert.Equal(2, result.Count);

            var gpt = result.First(m => m.Id == "gpt-5.4");
            Assert.Equal(1050000, gpt.ContextWindow);
            Assert.Equal(128000, gpt.MaxOutputTokens);
            Assert.Equal("gpt", gpt.Family);
            Assert.Equal("GPT 5.4", gpt.DisplayName);

            var claude = result.First(m => m.Id == "claude-sonnet-4-5");
            Assert.Equal(200000, claude.ContextWindow);
            Assert.Equal(64000, claude.MaxOutputTokens);
            Assert.Equal("claude", claude.Family);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task SaveModelsAsync_WithMetadata_GetModelIdsAsync_AlsoWorks()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store);

            var metas = new List<CloudProviderModelMeta>
            {
                new() { Id = "gpt-5.4", ContextWindow = 1050000 },
                new() { Id = "claude-sonnet-4-5", ContextWindow = 200000 }
            };
            await store.SaveModelsAsync(id, metas);

            // GetModelIdsAsync should still return bare IDs
            var ids = await store.GetModelIdsAsync(id);
            Assert.Equal(2, ids.Count);
            Assert.Contains("gpt-5.4", ids);
            Assert.Contains("claude-sonnet-4-5", ids);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task SaveModelsAsync_StringOverload_CreatesMetaObjects()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store);

            // Save via string overload
            await store.SaveModelsAsync(id, ["gpt-4o", "gpt-4o-mini"]);

            // Read back via meta overload — should get object-format entries
            var metas = await store.GetModelMetasAsync(id);
            Assert.Equal(2, metas.Count);
            Assert.Equal("gpt-4o", metas[0].Id);
            Assert.Equal(0, metas[0].ContextWindow); // default from FromId
            Assert.Equal("gpt-4o-mini", metas[1].Id);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task SaveModelsAsync_MetadataOverwrite_ReplacesPrevious()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store);

            // First save with metadata
            await store.SaveModelsAsync(id, new List<CloudProviderModelMeta>
            {
                new() { Id = "model-a", ContextWindow = 100000 }
            });

            // Overwrite with different metadata
            await store.SaveModelsAsync(id, new List<CloudProviderModelMeta>
            {
                new() { Id = "model-a", ContextWindow = 200000, Family = "updated" },
                new() { Id = "model-b", ContextWindow = 50000 }
            });

            var result = await store.GetModelMetasAsync(id);
            Assert.Equal(2, result.Count);
            var modelA = result.First(m => m.Id == "model-a");
            Assert.Equal(200000, modelA.ContextWindow);
            Assert.Equal("updated", modelA.Family);
            Assert.Equal("model-b", result[1].Id);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task GetModelMetasAsync_MissingProvider_ReturnsEmpty()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var metas = await store.GetModelMetasAsync("cp_nonexistent");
            Assert.Empty(metas);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task GetModelMetasAsync_DefaultModelsJson_ReturnsEmpty()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store);
            var metas = await store.GetModelMetasAsync(id);
            Assert.Empty(metas);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task SaveModelsAsync_Metadata_cloudPrefix_ThrowsArgumentException()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store);

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.SaveModelsAsync(id, new List<CloudProviderModelMeta>
                {
                    new() { Id = "cloud/some-model" }
                }));
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task SaveModelsAsync_Metadata_EmptyId_ThrowsArgumentException()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store);

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.SaveModelsAsync(id, new List<CloudProviderModelMeta>
                {
                    new() { Id = "" }
                }));
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task SaveModelsAsync_Metadata_TooMany_ThrowsArgumentException()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store);
            var tooMany = Enumerable.Range(1, 501)
                .Select(i => CloudProviderModelMeta.FromId($"model-{i}"))
                .ToList();

            await Assert.ThrowsAsync<ArgumentException>(
                () => store.SaveModelsAsync(id, tooMany));
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── SaveOAuthTokensAsync / GetOAuthTokensAsync ─────────────────────────

    [Fact]
    public async Task SaveOAuthTokensAsync_SavesTokens()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store, apiKey: "");
            var expiresAt = DateTimeOffset.UtcNow.AddHours(1);

            await store.SaveOAuthTokensAsync(id, "access-cipher", "refresh-cipher", expiresAt, "acct-123", CancellationToken.None);

            var tokens = await store.GetOAuthTokensAsync(id, CancellationToken.None);
            Assert.NotNull(tokens);
            Assert.Equal("access-cipher", tokens.AccessTokenCiphertext);
            Assert.Equal("refresh-cipher", tokens.RefreshTokenCiphertext);
            Assert.Equal(expiresAt, tokens.ExpiresAt);
            Assert.Equal("acct-123", tokens.ChatgptAccountId);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task SaveOAuthTokensAsync_MissingProvider_Throws()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.SaveOAuthTokensAsync("cp_nonexistent", "a", "r", null, null, CancellationToken.None));
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task GetOAuthTokensAsync_MissingProvider_ReturnsNull()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var tokens = await store.GetOAuthTokensAsync("cp_nonexistent", CancellationToken.None);
            Assert.Null(tokens);
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── GetAuthTypeAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetAuthTypeAsync_ReturnsAuthType()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store);
            // Default auth type is 0 (ApiKey)
            var authType = await store.GetAuthTypeAsync(id, CancellationToken.None);
            Assert.Equal(0, authType);
        }
        finally { await conn.DisposeAsync(); }
    }

    [Fact]
    public async Task GetAuthTypeAsync_MissingProvider_ReturnsZero()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var authType = await store.GetAuthTypeAsync("cp_nonexistent", CancellationToken.None);
            Assert.Equal(0, authType);
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── Model count in list ────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_ModelCount_ReflectsSavedModels()
    {
        var (factory, conn) = BuildDb();
        try
        {
            var store = new CloudProviderStore(factory, FakeEncryptor(), Log());
            var id = await CreateTestProviderAsync(store);
            await store.SaveModelsAsync(id, ["gpt-4o", "gpt-4o-mini"]);

            var list = await store.ListAsync();
            Assert.Single(list);
            Assert.Equal(2, list[0].ModelCount);
        }
        finally { await conn.DisposeAsync(); }
    }

    // ── Fakes ──────────────────────────────────────────────────────────────

    private sealed class FakeEncryptorImpl : IApiKeyEncryptor
    {
        public string Protect(string plaintext) => $"encrypted-{plaintext}";
        public string Unprotect(string ciphertext) =>
            ciphertext.StartsWith("encrypted-") ? ciphertext["encrypted-".Length..] : ciphertext;
    }

    private sealed class FakeApiKeyStore : IApiKeyStore
    {
        public List<string> RemovedProviderNames { get; } = [];

        public Task<int> RemoveProviderFromAllKeysAsync(string providerName, CancellationToken ct = default)
        {
            RemovedProviderNames.Add(providerName);
            return Task.FromResult(1);
        }

        // Stubs for unused methods
        public Task<Core.Models.CreateApiKeyResponse> CreateAsync(string name, Core.Models.ApiKeyScope scope = Core.Models.ApiKeyScope.Inference, string? explicitKey = null, string? boundAgentName = null, string? permissionsJson = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Core.Models.ApiKeyItem>> ListAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Core.Models.ApiKeyItem?> GetAsync(string id, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<bool> RevokeAsync(string id, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Core.Models.CreateApiKeyResponse> RotateAsync(string id, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Unswarm.Core.Persistence.ApiKeyEntity?> AuthenticateAsync(string presentedSecret, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<bool> HasAnyAsync(Core.Models.ApiKeyScope scope, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task UpdateLastUsedAsync(string id, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Core.Models.AgentKeyBindingResult> ResolveAgentBindingAsync(string keyId, string claimedAgentName, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Core.Models.KeyAccess?> GetAccessAsync(string keyId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Core.Models.KeyAccess?> GetAccessCachedAsync(string keyId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Core.Models.KeyAccess?> SaveAccessAsync(string keyId, Core.Models.KeyAccess access, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Dictionary<string, string>?> GetPermissionsAsync(string keyId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Dictionary<string, string>?> SavePermissionsAsync(string keyId, string permissionsJson, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
