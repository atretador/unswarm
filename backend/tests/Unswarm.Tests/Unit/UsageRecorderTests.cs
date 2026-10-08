using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Persistence;
using Unswarm.Core.Services;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Unit tests for the real <see cref="UsageRecorder"/>: DB persistence via a
/// throwaway scope, live-tail fan-out with agent/cloud cost-unit attribution,
/// the no-broadcaster path, and the fail-safe (never throw) path.
/// </summary>
public sealed class UsageRecorderTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public UsageRecorderTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateDb();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private UnswarmDbContext CreateDb()
        => new(new DbContextOptionsBuilder<UnswarmDbContext>().UseSqlite(_connection).Options);

    private static UsageRecorder CreateRecorder(IServiceScopeFactory scopeFactory)
        => new(scopeFactory, new LoggerFactory().CreateLogger<UsageRecorder>());

    private ServiceProvider BuildProvider(IUsageLiveTailBroadcaster? broadcaster = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<UnswarmDbContext>(_ => CreateDb());
        if (broadcaster is not null)
            services.AddSingleton(broadcaster);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task RecordAsync_PersistsUsageRow()
    {
        await using var provider = BuildProvider();
        var recorder = CreateRecorder(provider.GetRequiredService<IServiceScopeFactory>());

        await recorder.RecordAsync("openai", "gpt-4o", 10, 20, 3, isStreaming: true, elapsedMs: 123.4,
            apiKeyId: "k1", apiKeyName: "my-key", providerKind: "cloud");

        await using var db = CreateDb();
        var row = Assert.Single(db.UsageRecords.ToList());
        Assert.Equal("openai", row.Provider);
        Assert.Equal("gpt-4o", row.Model);
        Assert.Equal(10, row.PromptTokens);
        Assert.Equal(20, row.CompletionTokens);
        Assert.Equal(3, row.CachedTokens);
        Assert.True(row.IsStreaming);
        Assert.Equal(123, row.ElapsedMs);
        Assert.Equal("k1", row.ApiKeyId);
        Assert.Equal("my-key", row.ApiKeyName);
        Assert.Equal("cloud", row.ProviderKind);
    }

    [Fact]
    public async Task RecordAsync_LocalWithAgent_PublishesAgentCostUnit()
    {
        var broadcaster = new UsageLiveTailBroadcaster();
        using var subscription = broadcaster.Subscribe();
        await using var provider = BuildProvider(broadcaster);
        var recorder = CreateRecorder(provider.GetRequiredService<IServiceScopeFactory>());

        await recorder.RecordAsync("runtime-a", "llama-3", 1, 2, 0, false, 5.0, providerKind: "local", agent: "agent-x");

        Assert.True(subscription.Reader.TryRead(out var evt));
        Assert.Equal("agent-x", evt.Provider);
        Assert.Equal("local", evt.ProviderKind);
        Assert.Equal("agent-x", evt.Agent);
        Assert.Equal("llama-3", evt.Model);
    }

    [Fact]
    public async Task RecordAsync_Cloud_PublishesProviderCostUnit()
    {
        var broadcaster = new UsageLiveTailBroadcaster();
        using var subscription = broadcaster.Subscribe();
        await using var provider = BuildProvider(broadcaster);
        var recorder = CreateRecorder(provider.GetRequiredService<IServiceScopeFactory>());

        await recorder.RecordAsync("openai", "gpt-4o", 1, 2, 0, false, 5.0, providerKind: "cloud");

        Assert.True(subscription.Reader.TryRead(out var evt));
        Assert.Equal("openai", evt.Provider);
        Assert.Equal("cloud", evt.ProviderKind);
    }

    [Fact]
    public async Task RecordAsync_NoBroadcasterRegistered_StillPersists()
    {
        await using var provider = BuildProvider();
        var recorder = CreateRecorder(provider.GetRequiredService<IServiceScopeFactory>());

        await recorder.RecordAsync("openai", "gpt-4o", 1, 2, 0, false, 5.0, providerKind: "cloud");

        await using var db = CreateDb();
        Assert.Single(db.UsageRecords.ToList());
    }

    [Fact]
    public async Task RecordAsync_DbFailure_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddScoped<UnswarmDbContext>(_ => throw new InvalidOperationException("db down"));
        await using var provider = services.BuildServiceProvider();
        var recorder = CreateRecorder(provider.GetRequiredService<IServiceScopeFactory>());

        // Must swallow the failure (fire-and-forget callers).
        await recorder.RecordAsync("openai", "gpt-4o", 1, 2, 0, false, 5.0);
    }
}
