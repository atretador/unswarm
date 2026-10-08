using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Persistence;
using Unswarm.Core.Services;
using Unswarm.Tests.Fakes;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests the LogStore graceful-shutdown flush: entries queued but not yet written
/// by the background writer are persisted during Dispose (drain path).
/// </summary>
public sealed class LogStoreDrainTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"unswarm-logdrain-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly Func<UnswarmDbContext> _dbFactory;

    public LogStoreDrainTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "logs.db");
        _dbFactory = () => new UnswarmDbContext(
            new DbContextOptionsBuilder<UnswarmDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        using var db = _dbFactory();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Dispose_FlushesQueuedEntries()
    {
        var store = new LogStore(_dbFactory, new FakeClock(), new LoggerFactory().CreateLogger<LogStore>());

        for (var i = 0; i < 60; i++)
            store.Enqueue(LogLevel.Info, "drain", $"msg-{i}");

        store.Dispose();

        using var db = _dbFactory();
        Assert.Equal(60, db.Logs.Count(l => l.Source == "drain"));
    }

    [Fact]
    public void Dispose_WithNoEntries_DoesNotThrow()
    {
        var store = new LogStore(_dbFactory, new FakeClock(), new LoggerFactory().CreateLogger<LogStore>());
        store.Dispose();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var store = new LogStore(_dbFactory, new FakeClock(), new LoggerFactory().CreateLogger<LogStore>());
        store.Enqueue(LogLevel.Info, "drain", "x");

        store.Dispose();
        store.Dispose(); // second dispose must be a no-op, not ObjectDisposedException
    }
}
