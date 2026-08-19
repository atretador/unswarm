using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Core.Services;

public sealed class LogStore : ILogStore
{
    private readonly Func<UnswarmDbContext> _dbFactory;
    private readonly IClock _clock;
    private readonly ILogger<LogStore> _logger;

    // Bounded channel with DropOldest for live fan-out
    private readonly Channel<LogEntry> _liveChannel;
    private readonly List<Channel<LogEntry>> _subscribers = new();
    private readonly object _subscribersLock = new();

    public LogStore(Func<UnswarmDbContext> dbFactory, IClock clock, ILogger<LogStore> logger)
    {
        _dbFactory = dbFactory;
        _clock = clock;
        _logger = logger;
        _liveChannel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
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

        // Best-effort persist to SQLite (fire and forget)
        _ = PersistAsync(entry);
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

            var entities = await query
                .OrderByDescending(l => l.Timestamp)
                .Take(limit)
                .ToListAsync(ct).ConfigureAwait(false);

            return entities.Select(MapToEntry).ToList();
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

    private async Task PersistAsync(LogEntry entry)
    {
        try
        {
            await using var db = _dbFactory();
            db.Logs.Add(new LogEntity
            {
                Id = entry.Id,
                Timestamp = entry.Timestamp,
                Level = entry.Level.ToString(),
                Source = entry.Source,
                Message = entry.Message,
                MetadataJson = entry.Metadata is not null
                    ? JsonSerializer.Serialize(entry.Metadata)
                    : null
            });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort — log persistence failure to stderr to avoid recursion
            System.Diagnostics.Debug.WriteLine($"Log persist failed: {ex.Message}");
        }
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
