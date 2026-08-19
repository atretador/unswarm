using Unswarm.Core.Models;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Api.Dtos;

public sealed class LogEntryResponse
{
    public string Id { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public LogLevel Level { get; set; }
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
    public Dictionary<string, object>? Metadata { get; set; }

    public static LogEntryResponse FromEntry(LogEntry e) => new()
    {
        Id = e.Id,
        Timestamp = e.Timestamp,
        Level = e.Level,
        Source = e.Source,
        Message = e.Message,
        Metadata = e.Metadata
    };
}
