using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// Scriptable <see cref="ILogStore"/> for the SSE stream test: yields the
/// configured entries from <see cref="SubscribeAsync"/> and then completes.
/// </summary>
public sealed class FakeLogStoreCoverage1 : ILogStore
{
    public List<LogEntry> StreamEntries { get; } = [];

    public void Enqueue(LogLevel level, string source, string message, Dictionary<string, object>? metadata = null)
        => StreamEntries.Add(new LogEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = DateTimeOffset.UtcNow,
            Level = level,
            Source = source,
            Message = message
        });

    public Task<IReadOnlyList<LogEntry>> GetHistoricalAsync(
        string? source = null,
        LogLevel? level = null,
        int limit = 100,
        DateTimeOffset? since = null,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LogEntry>>(
            StreamEntries
                .Where(e => source is null || e.Source == source)
                .Where(e => level is null || e.Level == level)
                .Take(limit)
                .ToList());

    public async IAsyncEnumerable<LogEntry> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var entry in StreamEntries)
        {
            ct.ThrowIfCancellationRequested();
            yield return entry;
        }

        await Task.CompletedTask;
    }
}
