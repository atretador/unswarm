using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;
using Unswarm.Core.Services;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Unit tests for the real <see cref="SettingsStore"/> against in-memory SQLite:
/// default snapshot, persistence, cache warm/refresh behavior, and update of an
/// existing row.
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public SettingsStoreTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateDb();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private UnswarmDbContext CreateDb()
        => new(new DbContextOptionsBuilder<UnswarmDbContext>().UseSqlite(_connection).Options);

    private SettingsStore CreateStore() => new(CreateDb, new LoggerFactory().CreateLogger<SettingsStore>());

    [Fact]
    public async Task GetAsync_NoStoredSettings_ReturnsDefaults()
    {
        var store = CreateStore();

        var settings = await store.GetAsync();

        Assert.Equal(new Settings().IdleTimeout, settings.IdleTimeout);
        Assert.Equal(new Settings().MaxQueueDepth, settings.MaxQueueDepth);
    }

    [Fact]
    public async Task UpdateAsync_PersistsAndReturnsValues()
    {
        var store = CreateStore();
        var updated = new Settings
        {
            IdleTimeout = 123,
            MaxQueueDepth = 7,
            UsageRetentionDays = 5,
            RouterRetryAttempts = 4
        };

        var result = await store.UpdateAsync(updated);

        Assert.Equal(123, result.IdleTimeout);
        Assert.Equal(7, result.MaxQueueDepth);
        Assert.Equal(5, result.UsageRetentionDays);
        Assert.Equal(4, result.RouterRetryAttempts);

        // A second store reads the persisted row.
        var reread = await new SettingsStore(CreateDb, new LoggerFactory().CreateLogger<SettingsStore>()).GetAsync();
        Assert.Equal(123, reread.IdleTimeout);
        Assert.Equal(7, reread.MaxQueueDepth);
    }

    [Fact]
    public async Task UpdateAsync_Twice_UpdatesExistingRow()
    {
        var store = CreateStore();
        await store.UpdateAsync(new Settings { IdleTimeout = 10 });
        await store.UpdateAsync(new Settings { IdleTimeout = 20 });

        var settings = await store.GetAsync();
        Assert.Equal(20, settings.IdleTimeout);
    }

    [Fact]
    public async Task GetAsync_AfterUpdate_ServesCachedSnapshot()
    {
        var store = CreateStore();
        await store.UpdateAsync(new Settings { IdleTimeout = 42 });

        // Cached (no DB round-trip) — still the updated value.
        var cached = await store.GetAsync();
        Assert.Equal(42, cached.IdleTimeout);
    }

    [Fact]
    public async Task GetAsync_NoCacheAndDbFailure_Throws()
    {
        var store = new SettingsStore(
            () => throw new InvalidOperationException("db down"),
            new LoggerFactory().CreateLogger<SettingsStore>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync());
    }
}
