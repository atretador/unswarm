using Unswarm.Core.Models;

namespace Unswarm.Core.Contracts;

public interface ILogStore
{
    void Enqueue(LogLevel level, string source, string message, Dictionary<string, object>? metadata = null);
    Task<IReadOnlyList<LogEntry>> GetHistoricalAsync(
        string? source = null,
        LogLevel? level = null,
        int limit = 100,
        DateTimeOffset? since = null,
        CancellationToken ct = default);
    IAsyncEnumerable<LogEntry> SubscribeAsync(CancellationToken ct = default);
}
