using Unswarm.Core.Contracts;

namespace Unswarm.Api.Dtos;

/// <summary>
/// Wire shape for a benchmark run. Shared between POST result and GET list.
/// Note: TokensPerSec == 0 means "unknown" (the model did not report usable token
/// counts or the run errored) — UIs should render it as n/a rather than 0 tok/s.
/// </summary>
public sealed class BenchmarkResponse
{
    public string Id { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string? Prompt { get; set; }
    public double TokensPerSec { get; set; }
    public double LatencyMs { get; set; }
    public long TokensGenerated { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string Status { get; set; } = "completed";
    public string? ErrorMessage { get; set; }
    public string? PromptId { get; set; }
    public string? PromptName { get; set; }
    public int? PromptVersion { get; set; }

    /// <summary>LLM response text (truncated); null when unavailable.</summary>
    public string? Response { get; set; }

    public static BenchmarkResponse FromEntry(BenchmarkHistoryEntry e) => new()
    {
        Id = e.Id,
        ModelId = e.ModelId,
        ModelName = e.ModelId,
        Prompt = e.Prompt,
        TokensPerSec = e.TokensPerSec,
        LatencyMs = e.LatencyMs,
        TokensGenerated = e.TokensGenerated,
        Timestamp = e.Timestamp,
        Status = e.Status,
        ErrorMessage = e.ErrorMessage,
        PromptId = e.PromptId,
        PromptName = e.PromptName,
        PromptVersion = e.PromptVersion,
        Response = e.Response
    };
}
