namespace Unswarm.Core.Contracts;

/// <summary>A persisted benchmark run for a model.</summary>
public sealed record BenchmarkHistoryEntry
{
    public required string Id { get; init; }
    public required string ModelId { get; init; }
    public double TokensPerSec { get; init; }
    public double LatencyMs { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string? Prompt { get; init; }
    public long TokensGenerated { get; init; }
    public string Status { get; init; } = "completed";
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Persistence for model benchmark history (newest-first reads).
/// </summary>
public interface IBenchmarkHistory
{
    Task<BenchmarkHistoryEntry> AddAsync(
        string modelId,
        string? prompt,
        double tokensPerSec,
        double latencyMs,
        long tokensGenerated,
        string status,
        string? errorMessage,
        CancellationToken ct = default);

    Task<IReadOnlyList<BenchmarkHistoryEntry>> ListAsync(int maxCount = 50, CancellationToken ct = default);

    Task<BenchmarkHistoryEntry?> GetLatestForModelAsync(string modelId, CancellationToken ct = default);
}
