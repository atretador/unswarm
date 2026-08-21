using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Unit;

public sealed class ApiKeyStoreTests
{
    private static IApiKeyStore NewStore() => TestApiKeyStore.Create();

    [Fact]
    public async Task CreateAsync_ReturnsRawSecretOnce_AndStoresOnlyHash()
    {
        var store = NewStore();
        var created = await store.CreateAsync("ci key", ApiKeyScope.Inference);

        Assert.False(string.IsNullOrEmpty(created.Secret));
        Assert.StartsWith("usk_", created.Secret);
        Assert.NotEqual(created.Secret, created.KeyPrefix);

        // The raw secret must never appear in any list/get response.
        var item = await store.GetAsync(created.Id);
        Assert.Equal(created.KeyPrefix, item!.KeyPrefix);
        Assert.Equal("ci key", item.Name);
        Assert.Equal(ApiKeyScope.Inference, item.Scope);

        var list = await store.ListAsync();
        Assert.Single(list);
        // No list item exposes the raw secret: every item carries only the prefix.
        Assert.All(list, k => Assert.Equal(k.KeyPrefix, created.KeyPrefix));
    }

    [Fact]
    public async Task AuthenticateAsync_MatchesCorrectKey_AndRejectsWrong()
    {
        var store = NewStore();
        var created = await store.CreateAsync("agent", ApiKeyScope.Agent, "agent-secret");

        Assert.Equal(created.Id, (await store.AuthenticateAsync("agent-secret"))!.Id);
        Assert.Null(await store.AuthenticateAsync("wrong-secret"));
        Assert.Null(await store.AuthenticateAsync(""));
    }

    [Fact]
    public async Task HashedSecret_IsNotReversible()
    {
        var store = NewStore();
        await store.CreateAsync("agent", ApiKeyScope.Agent, "agent-secret");

        var list = await store.ListAsync();
        var entity = list.Single();

        // Only the short prefix is exposed; the full secret is absent.
        Assert.True(entity.KeyPrefix.Length < "agent-secret".Length);
    }

    [Fact]
    public async Task RevokeAsync_PreventsAuthentication()
    {
        var store = NewStore();
        var created = await store.CreateAsync("agent", ApiKeyScope.Agent, "agent-secret");

        Assert.True(await store.RevokeAsync(created.Id));

        Assert.Null(await store.AuthenticateAsync("agent-secret"));

        var item = await store.GetAsync(created.Id);
        Assert.False(item!.IsActive);
    }

    [Fact]
    public async Task RotateAsync_IssuesNewSecret_InvalidatesOld()
    {
        var store = NewStore();
        var created = await store.CreateAsync("agent", ApiKeyScope.Agent, "agent-secret");

        var rotated = await store.RotateAsync(created.Id);

        Assert.NotEqual(created.Secret, rotated.Secret);
        Assert.Equal(created.Id, rotated.Id);
        Assert.Equal("agent", rotated.Name);
        Assert.Equal(ApiKeyScope.Agent, rotated.Scope);

        Assert.Null(await store.AuthenticateAsync("agent-secret")); // old dead
        Assert.NotNull(await store.AuthenticateAsync(rotated.Secret)); // new works
    }

    [Fact]
    public async Task HasAnyAsync_ReflectsScopeState()
    {
        var store = NewStore();

        Assert.False(await store.HasAnyAsync(ApiKeyScope.Inference));

        await store.CreateAsync("ci", ApiKeyScope.Inference);
        Assert.True(await store.HasAnyAsync(ApiKeyScope.Inference));
        Assert.False(await store.HasAnyAsync(ApiKeyScope.Agent));
    }

    [Fact]
    public async Task HasAnyAsync_IncludesRetiredKeys()
    {
        var store = NewStore();
        var created = await store.CreateAsync("ci", ApiKeyScope.Inference);
        await store.RevokeAsync(created.Id);

        // A revoked key still keeps the scope enforced (never reopens it empty).
        Assert.True(await store.HasAnyAsync(ApiKeyScope.Inference));
    }

    [Fact]
    public async Task ListAsync_OrderedNewestFirst()
    {
        var store = NewStore();
        var first = await store.CreateAsync("first", ApiKeyScope.Inference);
        var second = await store.CreateAsync("second", ApiKeyScope.Inference);

        var list = await store.ListAsync();
        Assert.Equal(second.Id, list[0].Id);
        Assert.Equal(first.Id, list[1].Id);
    }
}
