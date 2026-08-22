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

    // ── Per-agent key binding ─────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_WithBoundAgentName_PersistsBinding()
    {
        var store = NewStore();
        var created = await store.CreateAsync("alpha key", ApiKeyScope.Agent, boundAgentName: "alpha");

        Assert.Equal("alpha", created.BoundAgentName);

        var item = await store.GetAsync(created.Id);
        Assert.NotNull(item);
        Assert.Equal("alpha", item!.BoundAgentName);
        Assert.Equal(ApiKeyScope.Agent, item.Scope);
    }

    [Fact]
    public async Task CreateAsync_WithoutBoundAgentName_StaysUnbound()
    {
        var store = NewStore();
        var created = await store.CreateAsync("free agent", ApiKeyScope.Agent);

        Assert.Null(created.BoundAgentName);

        var item = await store.GetAsync(created.Id);
        Assert.Null(item!.BoundAgentName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_BlankBoundAgentName_Throws(string boundAgentName)
    {
        var store = NewStore();
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.CreateAsync("bad", ApiKeyScope.Agent, boundAgentName: boundAgentName));
    }

    [Fact]
    public async Task ResolveAgentBinding_FirstUseBinds_AndSubsequentMismatchRejected()
    {
        var store = NewStore();
        var created = await store.CreateAsync("consumable", ApiKeyScope.Agent);

        // First use: unbound key binds to the claimed name.
        Assert.Equal(AgentKeyBindingResult.Allowed,
            await store.ResolveAgentBindingAsync(created.Id, "first-agent"));

        // Binding persisted immediately.
        var item = await store.GetAsync(created.Id);
        Assert.Equal("first-agent", item!.BoundAgentName);

        // Same name still allowed; any other name is rejected forever.
        Assert.Equal(AgentKeyBindingResult.Allowed,
            await store.ResolveAgentBindingAsync(created.Id, "first-agent"));
        Assert.Equal(AgentKeyBindingResult.Mismatch,
            await store.ResolveAgentBindingAsync(created.Id, "other-agent"));
    }

    [Fact]
    public async Task ResolveAgentBinding_KeyBoundAtCreation_RejectsOtherNamesImmediately()
    {
        var store = NewStore();
        var created = await store.CreateAsync("bound", ApiKeyScope.Agent, boundAgentName: "alpha");

        Assert.Equal(AgentKeyBindingResult.Allowed,
            await store.ResolveAgentBindingAsync(created.Id, "alpha"));
        Assert.Equal(AgentKeyBindingResult.Mismatch,
            await store.ResolveAgentBindingAsync(created.Id, "beta"));

        // Creation-time binding was never overwritten by the rejected claim.
        Assert.Equal("alpha", (await store.GetAsync(created.Id))!.BoundAgentName);
    }

    [Fact]
    public async Task ResolveAgentBinding_ConcurrentFirstUse_ExactlyOneNameWins()
    {
        // File-backed DB so each context gets its own connection — a real race.
        string dbPath = Path.Combine(Path.GetTempPath(), $"unswarm-keyrace-{Guid.NewGuid():N}.db");
        try
        {
            var store = TestApiKeyStore.Create(dbPath);
            var created = await store.CreateAsync("raced", ApiKeyScope.Agent);

            string[] claimants = ["agent-a", "agent-b", "agent-c", "agent-d"];
            var results = await Task.WhenAll(claimants.Select(
                name => store.ResolveAgentBindingAsync(created.Id, name)));

            Assert.Single(results, r => r == AgentKeyBindingResult.Allowed);
            Assert.Equal(3, results.Count(r => r == AgentKeyBindingResult.Mismatch));

            // The persisted binding is the single winner's name and is final.
            var winner = (await store.GetAsync(created.Id))!.BoundAgentName;
            Assert.Contains(winner, claimants);
            Assert.Equal(AgentKeyBindingResult.Allowed,
                await store.ResolveAgentBindingAsync(created.Id, winner!));
            Assert.Equal(AgentKeyBindingResult.Mismatch,
                await store.ResolveAgentBindingAsync(created.Id, "post-race-impostor"));
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var file = dbPath + suffix;
                if (File.Exists(file)) File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task ResolveAgentBinding_UnknownOrInactiveKey_Mismatches()
    {
        var store = NewStore();
        Assert.Equal(AgentKeyBindingResult.Mismatch,
            await store.ResolveAgentBindingAsync("no-such-key", "any"));

        var created = await store.CreateAsync("revoked", ApiKeyScope.Agent);
        await store.RevokeAsync(created.Id);
        Assert.Equal(AgentKeyBindingResult.Mismatch,
            await store.ResolveAgentBindingAsync(created.Id, "any"));
    }
}
