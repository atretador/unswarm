using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Unswarm.Core.Persistence;
using Unswarm.Core.Services;

namespace Unswarm.Tests.Unit;

public sealed class PromptStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Func<UnswarmDbContext> _dbFactory;

    public PromptStoreTests()
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

    private PromptStore CreateService() => new(_dbFactory);

    [Fact]
    public async Task CreateAsync_PersistsFullEntry()
    {
        var service = CreateService();

        var entry = await service.CreateAsync("My Prompt", "Write a summary of the text");

        Assert.NotNull(entry.Id);
        Assert.Equal("My Prompt", entry.Name);
        Assert.Equal("Write a summary of the text", entry.Text);
        Assert.True(entry.CreatedAt <= DateTimeOffset.UtcNow);
        Assert.Equal(entry.CreatedAt, entry.UpdatedAt);
    }

    [Fact]
    public async Task ListAsync_ReturnsOrderedByName()
    {
        var service = CreateService();
        await service.CreateAsync("Banana", "text1");
        await service.CreateAsync("Apple", "text2");
        await service.CreateAsync("Cherry", "text3");

        var list = await service.ListAsync();

        Assert.Equal(3, list.Count);
        Assert.Equal("Apple", list[0].Name);
        Assert.Equal("Banana", list[1].Name);
        Assert.Equal("Cherry", list[2].Name);
    }

    [Fact]
    public async Task ListAsync_Empty_WhenNoPrompts()
    {
        var service = CreateService();
        var list = await service.ListAsync();
        Assert.Empty(list);
    }

    [Fact]
    public async Task GetAsync_ReturnsCorrectEntry()
    {
        var service = CreateService();
        var created = await service.CreateAsync("Test", "body");

        var fetched = await service.GetAsync(created.Id);

        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched!.Id);
        Assert.Equal("Test", fetched.Name);
        Assert.Equal("body", fetched.Text);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNotFound()
    {
        var service = CreateService();
        var result = await service.GetAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAsync_ModifiesEntry()
    {
        var service = CreateService();
        var created = await service.CreateAsync("Old Name", "Old Text");

        var updated = await service.UpdateAsync(created.Id, "New Name", "New Text");

        Assert.NotNull(updated);
        Assert.Equal("New Name", updated!.Name);
        Assert.Equal("New Text", updated.Text);
        Assert.True(updated.UpdatedAt > created.CreatedAt);
    }

    [Fact]
    public async Task UpdateAsync_ReturnsNull_WhenNotFound()
    {
        var service = CreateService();
        var result = await service.UpdateAsync("nonexistent", "name", "text");
        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_RemovesEntry()
    {
        var service = CreateService();
        var created = await service.CreateAsync("Delete Me", "body");

        var deleted = await service.DeleteAsync(created.Id);

        Assert.True(deleted);
        Assert.Null(await service.GetAsync(created.Id));
    }

    [Fact]
    public async Task DeleteAsync_ReturnsFalse_WhenNotFound()
    {
        var service = CreateService();
        var result = await service.DeleteAsync("nonexistent");
        Assert.False(result);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
