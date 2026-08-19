using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;
using Unswarm.Core.Services;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class ContainerRegistryTests : IDisposable
{
    private readonly FakeClock _clock = new();
    private readonly SqliteConnection _connection;

    public ContainerRegistryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateDb();
        db.Database.EnsureCreated();
    }

    private UnswarmDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<UnswarmDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new UnswarmDbContext(options);
    }

    private ContainerRegistry CreateRegistry()
    {
        return new ContainerRegistry(
            CreateDb,
            _clock,
            new LoggerFactory().CreateLogger<ContainerRegistry>());
    }

    private async Task CreateModelInDb(string modelId, string name = "test-model")
    {
        await using var db = CreateDb();
        db.Models.Add(new ModelEntity
        {
            Id = modelId,
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static RegisteredContainer MakeContainer(
        string? id = null,
        string displayName = "Test",
        string image = "test:latest") => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"),
        DisplayName = displayName,
        Image = image,
        ContainerPort = 8080,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task CreateAsync_StoresContainer()
    {
        var registry = CreateRegistry();
        var container = MakeContainer(id: "c1", displayName: "First");

        var result = await registry.CreateAsync(container);

        Assert.Equal("c1", result.Id);
        Assert.Equal("First", result.DisplayName);

        var fetched = await registry.GetAsync("c1");
        Assert.NotNull(fetched);
        Assert.Equal("First", fetched.DisplayName);
    }

    [Fact]
    public async Task ListAllAsync_ReturnsAllContainers()
    {
        var registry = CreateRegistry();
        await registry.CreateAsync(MakeContainer(id: "c1", displayName: "Alpha"));
        await registry.CreateAsync(MakeContainer(id: "c2", displayName: "Beta"));

        var list = await registry.ListAllAsync();

        Assert.Equal(2, list.Count);
        Assert.Equal("Alpha", list[0].DisplayName);
        Assert.Equal("Beta", list[1].DisplayName);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNotFound()
    {
        var registry = CreateRegistry();
        var result = await registry.GetAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAsync_ModifiesContainer()
    {
        var registry = CreateRegistry();
        await registry.CreateAsync(MakeContainer(id: "c1"));

        var updated = MakeContainer(id: "c1", displayName: "Updated");
        updated = updated with { Status = ContainerRegistrationStatus.Ready };
        var result = await registry.UpdateAsync("c1", updated);

        Assert.Equal("Updated", result.DisplayName);
        Assert.Equal(ContainerRegistrationStatus.Ready, result.Status);

        var fetched = await registry.GetAsync("c1");
        Assert.Equal("Updated", fetched!.DisplayName);
    }

    [Fact]
    public async Task DeleteAsync_RemovesContainer()
    {
        var registry = CreateRegistry();
        await registry.CreateAsync(MakeContainer(id: "c1"));

        await registry.DeleteAsync("c1");

        var result = await registry.GetAsync("c1");
        Assert.Null(result);
    }

    [Fact]
    public async Task AddModelMappingAsync_StoresMapping()
    {
        var registry = CreateRegistry();
        await registry.CreateAsync(MakeContainer(id: "c1"));
        await CreateModelInDb("model-1");

        await registry.AddModelMappingAsync("c1", "model-1");

        var modelIds = await registry.GetModelIdsForContainerAsync("c1");
        Assert.Single(modelIds);
        Assert.Equal("model-1", modelIds[0]);
    }

    [Fact]
    public async Task AddModelMappingAsync_DuplicateDoesNotDuplicate()
    {
        var registry = CreateRegistry();
        await registry.CreateAsync(MakeContainer(id: "c1"));
        await CreateModelInDb("model-1");

        await registry.AddModelMappingAsync("c1", "model-1");
        await registry.AddModelMappingAsync("c1", "model-1");

        var modelIds = await registry.GetModelIdsForContainerAsync("c1");
        Assert.Single(modelIds);
    }

    [Fact]
    public async Task RemoveModelMappingAsync_RemovesMapping()
    {
        var registry = CreateRegistry();
        await registry.CreateAsync(MakeContainer(id: "c1"));
        await CreateModelInDb("model-1");
        await registry.AddModelMappingAsync("c1", "model-1");

        await registry.RemoveModelMappingAsync("c1", "model-1");

        var modelIds = await registry.GetModelIdsForContainerAsync("c1");
        Assert.Empty(modelIds);
    }

    [Fact]
    public async Task GetContainerIdForModelAsync_ReturnsContainerId()
    {
        var registry = CreateRegistry();
        await registry.CreateAsync(MakeContainer(id: "c1"));
        await CreateModelInDb("model-1");
        await registry.AddModelMappingAsync("c1", "model-1");

        var containerId = await registry.GetContainerIdForModelAsync("model-1");

        Assert.Equal("c1", containerId);
    }

    [Fact]
    public async Task GetContainerIdForModelAsync_ReturnsNull_WhenNotFound()
    {
        var registry = CreateRegistry();
        var containerId = await registry.GetContainerIdForModelAsync("unknown");
        Assert.Null(containerId);
    }

    [Fact]
    public async Task ExtraLabels_RoundTripsCorrectly()
    {
        var registry = CreateRegistry();
        var container = MakeContainer(id: "c1") with
        {
            ExtraLabels = new Dictionary<string, string> { ["gpu"] = "nvidia", ["env"] = "prod" }
        };

        await registry.CreateAsync(container);
        var fetched = await registry.GetAsync("c1");

        Assert.NotNull(fetched);
        Assert.Equal("nvidia", fetched.ExtraLabels["gpu"]);
        Assert.Equal("prod", fetched.ExtraLabels["env"]);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
