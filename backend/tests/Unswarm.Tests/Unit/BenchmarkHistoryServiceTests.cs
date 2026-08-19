using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Unswarm.Core.Contracts;
using Unswarm.Core.Persistence;
using Unswarm.Core.Services;

namespace Unswarm.Tests.Unit;

public sealed class BenchmarkHistoryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Func<UnswarmDbContext> _dbFactory;

    public BenchmarkHistoryServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbFactory = () =>
        {
            var options = new DbContextOptionsBuilder<UnswarmDbContext>()
                .UseSqlite(_connection)
                .Options;
            return new UnswarmDbContext(options);
        };
        using var db = _dbFactory();
        db.Database.EnsureCreated();
    }

    private BenchmarkHistoryService CreateService() => new(_dbFactory);

    private async Task SeedModel(string modelId)
    {
        await using var db = _dbFactory();
        db.Models.Add(new ModelEntity
        {
            Id = modelId,
            Name = modelId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AddAsync_PersistsFullEntry()
    {
        await SeedModel("model-1");
        var service = CreateService();

        var entry = await service.AddAsync(
            "model-1", "Some prompt", 12.5, 340.2, 42, "completed", null);

        Assert.NotNull(entry.Id);
        Assert.Equal("model-1", entry.ModelId);
        Assert.Equal("Some prompt", entry.Prompt);
        Assert.Equal(12.5, entry.TokensPerSec);
        Assert.Equal(340.2, entry.LatencyMs);
        Assert.Equal(42, entry.TokensGenerated);
        Assert.Equal("completed", entry.Status);
        Assert.Null(entry.ErrorMessage);
    }

    [Fact]
    public async Task AddAsync_PersistsErrorEntry()
    {
        await SeedModel("model-1");
        var service = CreateService();

        var entry = await service.AddAsync(
            "model-1", "Some prompt", 0, 10, 0, "error", "boom");

        Assert.Equal("error", entry.Status);
        Assert.Equal("boom", entry.ErrorMessage);
    }

    [Fact]
    public async Task ListAsync_NewestFirst_RespectsMaxCount()
    {
        await SeedModel("model-1");
        var service = CreateService();

        // Add a few with slightly different timestamps; the service uses DB time
        // so ordering is by insertion order for same-timestamp rows.
        for (var i = 0; i < 5; i++)
        {
            await service.AddAsync("model-1", $"p{i}", 10 + i, 100 + i, 10 + i, "completed", null);
        }

        var list = await service.ListAsync(maxCount: 3);

        Assert.Equal(3, list.Count);
        // Newest first: the last-inserted entry has the latest timestamp.
        Assert.Equal(14, list[0].TokensGenerated);
    }

    [Fact]
    public async Task ListAsync_DefaultsToFifty()
    {
        await SeedModel("model-1");
        var service = CreateService();
        for (var i = 0; i < 60; i++)
        {
            await service.AddAsync("model-1", $"p{i}", i, i, i, "completed", null);
        }

        var list = await service.ListAsync();

        Assert.Equal(50, list.Count);
    }

    [Fact]
    public async Task GetLatestForModelAsync_ReturnsLatestPerModel()
    {
        await SeedModel("model-a");
        await SeedModel("model-b");
        var service = CreateService();
        await service.AddAsync("model-a", "a1", 1, 1, 1, "completed", null);
        await service.AddAsync("model-b", "b1", 1, 1, 1, "completed", null);
        await service.AddAsync("model-a", "a2", 9, 9, 9, "completed", null);

        var latestA = await service.GetLatestForModelAsync("model-a");
        var latestB = await service.GetLatestForModelAsync("model-b");

        Assert.NotNull(latestA);
        Assert.Equal("a2", latestA!.Prompt);
        Assert.Equal(9, latestA.TokensGenerated);
        Assert.Equal("b1", latestB!.Prompt);
    }

    [Fact]
    public async Task GetLatestForModelAsync_ReturnsNull_WhenNone()
    {
        await SeedModel("model-1");
        var service = CreateService();
        var result = await service.GetLatestForModelAsync("model-1");
        Assert.Null(result);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
