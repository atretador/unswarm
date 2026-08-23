using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.E2ETests.Fakes;

public sealed class FakeLogStore : ILogStore
{
    public List<(LogLevel Level, string Source, string Message)> Entries { get; } = [];

    public void Enqueue(LogLevel level, string source, string message, Dictionary<string, object>? metadata = null)
    {
        lock (Entries) Entries.Add((level, source, message));
    }

    public Task<IReadOnlyList<LogEntry>> GetHistoricalAsync(
        string? source = null,
        LogLevel? level = null,
        int limit = 100,
        DateTimeOffset? since = null,
        CancellationToken ct = default)
    {
        IReadOnlyList<LogEntry> result;
        lock (Entries)
        {
            result = Entries
                .Select((e, i) => new LogEntry
                {
                    Id = i.ToString(),
                    Timestamp = DateTimeOffset.UtcNow,
                    Level = e.Level,
                    Source = e.Source,
                    Message = e.Message
                })
                .Where(e => source == null || e.Source == source)
                .Where(e => level == null || e.Level == level)
                .Take(limit)
                .ToList();
        }
        return Task.FromResult(result);
    }

    public async IAsyncEnumerable<LogEntry> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
