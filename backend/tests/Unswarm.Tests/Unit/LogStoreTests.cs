using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;
using Unswarm.Core.Services;
using Unswarm.Tests.Fakes;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Real LogStore (SQLite in-memory): enqueue + persistence, historical queries with
/// filters, live subscriber fan-out, and the subscriber cap.
/// </summary>
public sealed class LogStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Func<UnswarmDbContext> _dbFactory;
    private readonly FakeClock _clock = new();
    private readonly LogStore _store;

    public LogStoreTests()
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

        _store = new LogStore(_dbFactory, _clock, new LoggerFactory().CreateLogger<LogStore>());
    }

    private async Task<IReadOnlyList<LogEntry>> HistoricalAsync(
        string? source = null, LogLevel? level = null, int limit = 100)
        => await _store.GetHistoricalAsync(source, level, limit);

    /// <summary>
    /// Polls a historical query until <paramref name="match"/> holds. GetHistoricalAsync
    /// is best-effort (transient SQLite errors yield an empty list) and visibility lags
    /// Enqueue by up to one batch cycle, so a single immediate read can flake under
    /// parallel test load; retry within the Eventually window instead.
    /// </summary>
    private async Task<IReadOnlyList<LogEntry>> HistoricalEventuallyAsync(
        Func<IReadOnlyList<LogEntry>, bool> match,
        string? source = null, LogLevel? level = null, int limit = 100)
    {
        IReadOnlyList<LogEntry> result = [];
        await Eventually.UntilAsync(() =>
        {
            result = _store.GetHistoricalAsync(source, level, limit).GetAwaiter().GetResult();
            return match(result);
        });
        return result;
    }

    private int PersistedCount()
    {
        using var db = _dbFactory();
        return db.Logs.Count();
    }

    [Fact]
    public async Task Enqueue_PersistsEntryWithTimestampAndMetadata()
    {
        var metadata = new Dictionary<string, object> { ["requestId"] = "req-42" };
        _clock.UtcNow = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);

        _store.Enqueue(LogLevel.Warn, "scheduler", "switch took long", metadata);

        await Eventually.UntilAsync(() => PersistedCount() == 1);

        var entry = (await HistoricalAsync()).Single();
        Assert.Equal(LogLevel.Warn, entry.Level);
        Assert.Equal("scheduler", entry.Source);
        Assert.Equal("switch took long", entry.Message);
        Assert.Equal(new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero), entry.Timestamp);
        Assert.NotNull(entry.Metadata);
    }

    [Fact]
    public async Task GetHistorical_FiltersBySourceLevelAndLimit()
    {
        _clock.UtcNow = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _store.Enqueue(LogLevel.Info, "alpha", "a1");
        _clock.UtcNow = new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero);
        _store.Enqueue(LogLevel.Warn, "alpha", "a2");
        _clock.UtcNow = new DateTimeOffset(2025, 1, 3, 0, 0, 0, TimeSpan.Zero);
        _store.Enqueue(LogLevel.Info, "beta", "b1");

        await Eventually.UntilAsync(() => PersistedCount() == 3);

        var alphaOnly = await HistoricalEventuallyAsync(
            r => r.Count == 2 && r.All(e => e.Source == "alpha"), source: "alpha");
        Assert.Equal(2, alphaOnly.Count);
        Assert.All(alphaOnly, e => Assert.Equal("alpha", e.Source));

        var warns = await HistoricalEventuallyAsync(
            r => r.Count == 1, level: LogLevel.Warn);
        var warn = Assert.Single(warns);
        Assert.Equal("a2", warn.Message);

        var limited = await HistoricalEventuallyAsync(
            r => r.Count == 1, limit: 1);
        Assert.Single(limited);
        // Most recent first.
        Assert.Equal("b1", limited[0].Message);
    }

    [Fact]
    public async Task GetHistorical_OrdersMostRecentFirst()
    {
        _clock.UtcNow = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _store.Enqueue(LogLevel.Info, "s", "first");
        _clock.UtcNow = new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero);
        _store.Enqueue(LogLevel.Info, "s", "second");
        _clock.UtcNow = new DateTimeOffset(2025, 1, 3, 0, 0, 0, TimeSpan.Zero);
        _store.Enqueue(LogLevel.Info, "s", "third");

        await Eventually.UntilAsync(() => PersistedCount() == 3);

        var entries = await HistoricalEventuallyAsync(
            r => r.Count == 3 && r[0].Message == "third");
        Assert.Equal(["third", "second", "first"], entries.Select(e => e.Message).ToList());
    }

    [Fact]
    public async Task Subscribe_ReceivesLiveEntries()
    {
        using var cts = new CancellationTokenSource();
        var enumerator = _store.SubscribeAsync(cts.Token).GetAsyncEnumerator();

        var firstMove = enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        _store.Enqueue(LogLevel.Error, "proxy", "live entry");

        var moved = await firstMove;
        Assert.True(moved);
        Assert.Equal("live entry", enumerator.Current.Message);
        Assert.Equal(LogLevel.Error, enumerator.Current.Level);

        await enumerator.DisposeAsync();
        cts.Cancel();
    }

    [Fact]
    public async Task Subscribe_MoreThanTenConcurrent_Throws()
    {
        using var cts = new CancellationTokenSource();
        var enumerators = new List<IAsyncEnumerator<LogEntry>>();
        try
        {
            // Start 10 subscribers; registration happens synchronously on first MoveNext.
            var pending = new List<Task<bool>>();
            for (var i = 0; i < 10; i++)
            {
                var e = _store.SubscribeAsync(cts.Token).GetAsyncEnumerator();
                enumerators.Add(e);
                pending.Add(e.MoveNextAsync().AsTask());
            }

            // The 11th subscriber exceeds the cap.
            var eleventh = _store.SubscribeAsync(cts.Token).GetAsyncEnumerator();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => eleventh.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("Too many log subscribers", ex.Message);

            // Unblock and reap the 10 waiting subscribers.
            _store.Enqueue(LogLevel.Info, "s", "unblock");
            foreach (var p in pending)
                await p.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            foreach (var e in enumerators)
                await e.DisposeAsync();
            cts.Cancel();
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
