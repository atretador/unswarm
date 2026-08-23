using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;
using Unswarm.Core.Services;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests for ApiKeyAccessService — the per-key model access rules enforced on
/// /v1: empty allow-lists are unrestricted, cloud models match by provider name
/// or exact id, local models match by exact id or owning runtime display name,
/// and unknown keys fail closed.
/// </summary>
public sealed class ApiKeyAccessServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeContainerRegistry _registry = new();

    public ApiKeyAccessServiceTests()
    {
        _connection.Open();
        using var db = NewDbContext();
        db.Database.EnsureCreated();
    }

    private UnswarmDbContext NewDbContext() => new(
        new DbContextOptionsBuilder<UnswarmDbContext>()
            .UseSqlite(_connection)
            .Options);

    private ApiKeyAccessService CreateService()
    {
        var factory = () => NewDbContext();
        return new ApiKeyAccessService(factory, _registry);
    }

    /// <summary>Inserts a key row with raw AccessJson and returns its id.</summary>
    private async Task<string> SeedKeyAsync(string accessJson)
    {
        await using var db = NewDbContext();
        var entity = new ApiKeyEntity
        {
            Id = "key-" + Guid.NewGuid().ToString("N"),
            Name = "test key",
            KeyHash = Guid.NewGuid().ToString("N"),
            KeyPrefix = "usk_te",
            Scope = ApiKeyScope.Inference,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            AccessJson = accessJson
        };
        db.ApiKeys.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    private async Task SeedLocalRuntimeAsync(string runtimeId, string displayName, params string[] models)
    {
        await _registry.CreateAsync(new RegisteredRuntime
        {
            Id = runtimeId,
            DisplayName = displayName,
            Image = $"{runtimeId}:latest",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        foreach (var model in models)
            await _registry.AddModelMappingAsync(runtimeId, model);
    }

    [Fact]
    public async Task EmptyAccessLists_AreUnrestricted()
    {
        var keyId = await SeedKeyAsync("{}");
        var svc = CreateService();

        Assert.True(await svc.IsModelAllowedAsync(keyId, "cloud/openai/gpt-4o"));
        Assert.True(await svc.IsModelAllowedAsync(keyId, "llama3-8b"));
    }

    [Fact]
    public async Task CloudProviderAllowList_AllowsItsModels_AndRejectsOthers()
    {
        var keyId = await SeedKeyAsync("""{"providers":["openai"],"models":[]}""");
        var svc = CreateService();

        Assert.True(await svc.IsModelAllowedAsync(keyId, "cloud/openai/gpt-4o"));
        Assert.True(await svc.IsModelAllowedAsync(keyId, "cloud/OpenAI/gpt-4o-mini")); // case-insensitive
        Assert.False(await svc.IsModelAllowedAsync(keyId, "cloud/anthropic/claude-3"));
    }

    [Fact]
    public async Task ExactCloudModelId_InModels_AllowsEvenWhenProviderNotListed()
    {
        var keyId = await SeedKeyAsync("""{"providers":["openai"],"models":["cloud/anthropic/claude-3-haiku"]}""");
        var svc = CreateService();

        Assert.True(await svc.IsModelAllowedAsync(keyId, "cloud/anthropic/claude-3-haiku"));
        Assert.False(await svc.IsModelAllowedAsync(keyId, "cloud/anthropic/claude-3-opus"));
    }

    [Fact]
    public async Task LocalModel_MatchesByOwningRuntimeDisplayName_OrExactId()
    {
        await SeedLocalRuntimeAsync("rt-1", "GPU Box A", "llama3-8b", "qwen2-7b");
        var keyId = await SeedKeyAsync("""{"providers":["GPU Box A"],"models":["mistral-7b"]}""");
        var svc = CreateService();

        // Runtime display name in providers → all its mapped models allowed.
        Assert.True(await svc.IsModelAllowedAsync(keyId, "llama3-8b"));
        Assert.True(await svc.IsModelAllowedAsync(keyId, "qwen2-7b"));

        // Exact local model id in models → allowed even on another runtime.
        await SeedLocalRuntimeAsync("rt-2", "GPU Box B", "mistral-7b");
        Assert.True(await svc.IsModelAllowedAsync(keyId, "mistral-7b"));

        // Unknown local model → denied.
        Assert.False(await svc.IsModelAllowedAsync(keyId, "gemma-2b"));
    }

    [Fact]
    public async Task LocalModel_OnUnknownRuntime_IsDenied()
    {
        var keyId = await SeedKeyAsync("""{"providers":["Ghost Box"],"models":[]}""");
        var svc = CreateService();

        Assert.False(await svc.IsModelAllowedAsync(keyId, "some-model"));
    }

    [Fact]
    public async Task UnknownOrRevokedKey_FailsClosed()
    {
        var svc = CreateService();
        Assert.False(await svc.IsModelAllowedAsync("no-such-key", "cloud/openai/gpt-4o"));
    }

    [Fact]
    public async Task MalformedAccessJson_IsTreatedAsUnrestricted()
    {
        var keyId = await SeedKeyAsync("not-json-at-all");
        var svc = CreateService();

        Assert.True(await svc.IsModelAllowedAsync(keyId, "cloud/openai/gpt-4o"));
    }

    [Fact]
    public async Task GetAccess_ReturnsNull_ForUnknownKey_AndParsedListsOtherwise()
    {
        var svc = CreateService();
        Assert.Null(await svc.GetAccessAsync("missing"));

        var keyId = await SeedKeyAsync("""{"providers":["openai"],"models":["llama3-8b"]}""");
        var access = await svc.GetAccessAsync(keyId);
        Assert.NotNull(access);
        Assert.Equal(["openai"], access!.Providers);
        Assert.Equal(["llama3-8b"], access.Models);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
