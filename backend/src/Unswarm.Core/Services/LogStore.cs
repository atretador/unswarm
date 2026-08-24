using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Core.Services;

/// <summary>
/// In-memory log hub with a single background persistence writer.
///
/// Enqueue fans entries out to live subscribers and hands them to an internal
/// channel; ONE background task drains that channel and inserts entries in
/// batches (a single SaveChanges per batch of up to <see cref="BatchSize"/>,
/// flushed as soon as the channel is momentarily empty or the batch is full).
/// This replaces the previous one-transaction-per-entry pattern, which cost
/// 4–6 SQLite transactions per inference request.
///
/// Read-your-writes: under sustained load GetHistoricalAsync may lag behind
/// Enqueue by up to one batch cycle (bounded by <see cref="BatchSize"/> inserts,
/// typically well under 500ms); idle traffic flushes immediately.
/// </summary>
public sealed class LogStore : ILogStore, IDisposable
{
    private readonly Func<UnswarmDbContext> _dbFactory;
    private readonly IClock _clock;
    private readonly ILogger<LogStore> _logger;

    // Bounded channel with DropOldest for live fan-out
    private readonly Channel<LogEntry> _liveChannel;
    private readonly List<Channel<LogEntry>> _subscribers = new();
    private readonly object _subscribersLock = new();
    private const int MaxSubscribers = 10;

    /// <summary>Persist queue: bounded so a DB stall cannot grow memory without limit.</summary>
    private readonly Channel<LogEntity> _persistChannel;

    /// <summary>Max entries per persisted batch (one transaction each).</summary>
    private const int BatchSize = 50;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _writerTask;

    public LogStore(Func<UnswarmDbContext> dbFactory, IClock clock, ILogger<LogStore> logger)
    {
        _dbFactory = dbFactory;
        _clock = clock;
        _logger = logger;
        _liveChannel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _persistChannel = Channel.CreateBounded<LogEntity>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _writerTask = Task.Run(PersistLoopAsync);
    }

    public void Enqueue(LogLevel level, string source, string message, Dictionary<string, object>? metadata = null)
    {
        var entry = new LogEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = _clock.UtcNow,
            Level = level,
            Source = source,
            Message = message,
            Metadata = metadata
        };

        // Fan-out to live subscribers
        _liveChannel.Writer.TryWrite(entry);
        lock (_subscribersLock)
        {
            foreach (var sub in _subscribers)
            {
                sub.Writer.TryWrite(entry);
            }
        }

        // Hand off to the single background writer (batched inserts; never blocks).
        _persistChannel.Writer.TryWrite(new LogEntity
        {
            Id = entry.Id,
            Timestamp = entry.Timestamp,
            TimestampTicks = entry.Timestamp.UtcTicks,
            Level = entry.Level.ToString(),
            Source = entry.Source,
            Message = entry.Message,
            MetadataJson = entry.Metadata is not null
                ? JsonSerializer.Serialize(entry.Metadata)
                : null
        });
    }

    public async Task<IReadOnlyList<LogEntry>> GetHistoricalAsync(
        string? source = null,
        LogLevel? level = null,
        int limit = 100,
        DateTimeOffset? since = null,
        CancellationToken ct = default)
    {
        try
        {
            await using var db = _dbFactory();
            var query = db.Logs.AsQueryable();

            if (source is not null)
                query = query.Where(l => l.Source == source);
            if (level.HasValue)
                query = query.Where(l => l.Level == level.Value.ToString());
            if (since.HasValue)
                query = query.Where(l => l.Timestamp >= since.Value);

            // The SQLite EF provider cannot ORDER BY a DateTimeOffset column
            // (NotSupportedException at query compile time), so ordering uses
            // the TimestampTicks mirror column (UtcTicks — same ordering) and
            // the limit is applied in SQL: only the newest `limit` rows are
            // materialized instead of the whole filtered table.
            var matched = await query
                .OrderByDescending(l => l.TimestampTicks)
                .Take(limit)
                .ToListAsync(ct).ConfigureAwait(false);

            return matched
                .Select(MapToEntry)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read logs from database");
            return [];
        }
    }

    public async IAsyncEnumerable<LogEntry> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        lock (_subscribersLock)
        {
            if (_subscribers.Count >= MaxSubscribers)
                throw new InvalidOperationException("Too many log subscribers");
            _subscribers.Add(channel);
        }

        try
        {
            await foreach (var entry in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return entry;
            }
        }
        finally
        {
            lock (_subscribersLock)
            {
                _subscribers.Remove(channel);
            }
        }
    }

    /// <summary>
    /// Single background writer: waits for at least one queued entry, then drains
    /// up to <see cref="BatchSize"/> more without blocking, and persists the whole
    /// batch in one transaction. Draining until the channel is momentarily empty
    /// means low-volume traffic flushes immediately while bursts amortize into
    /// ≤ BatchSize-entry transactions (~500ms worst-case visibility lag under
    /// continuous saturation).
    /// </summary>
    private async Task PersistLoopAsync()
    {
        try
        {
            while (await _persistChannel.Reader.WaitToReadAsync(_shutdownCts.Token).ConfigureAwait(false))
            {
                var batch = new List<LogEntity>(BatchSize);
                while (batch.Count < BatchSize && _persistChannel.Reader.TryRead(out var entity))
                {
                    batch.Add(entity);
                }

                await PersistBatchAsync(batch).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            // Graceful drain on dispose: flush whatever is still queued.
            await DrainPendingAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Log persistence writer crashed; remaining queued entries are dropped");
        }
    }

    private async Task DrainPendingAsync()
    {
        var batch = new List<LogEntity>(BatchSize);
        while (_persistChannel.Reader.TryRead(out var entity))
        {
            batch.Add(entity);
            if (batch.Count >= BatchSize)
            {
                await PersistBatchAsync(batch).ConfigureAwait(false);
                batch = new List<LogEntity>(BatchSize);
            }
        }

        if (batch.Count > 0)
            await PersistBatchAsync(batch).ConfigureAwait(false);
    }

    private async Task PersistBatchAsync(List<LogEntity> batch)
    {
        if (batch.Count == 0)
            return;

        try
        {
            await using var db = _dbFactory();
            db.Logs.AddRange(batch);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort — log persistence failure to stderr to avoid recursion
            System.Diagnostics.Debug.WriteLine($"Log persist failed: {ex.Message}");
        }
    }

    /// <summary>Graceful shutdown: stop accepting entries and flush the queue.</summary>
    public void Dispose()
    {
        _persistChannel.Writer.TryComplete();
        _shutdownCts.Cancel();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Best-effort drain; never throw from Dispose.
        }

        _shutdownCts.Dispose();
    }

    private static LogEntry MapToEntry(LogEntity e) => new()
    {
        Id = e.Id,
        Timestamp = e.Timestamp,
        Level = Enum.TryParse<LogLevel>(e.Level, out var lvl) ? lvl : LogLevel.Info,
        Source = e.Source,
        Message = e.Message,
        Metadata = e.MetadataJson is not null
            ? JsonSerializer.Deserialize<Dictionary<string, object>>(e.MetadataJson)
            : null
    };
}
