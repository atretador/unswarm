namespace Unswarm.Core.Models;

public sealed class LogEntry
{
    public required string Id { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public LogLevel Level { get; init; }
    public required string Source { get; init; }
    public required string Message { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}
