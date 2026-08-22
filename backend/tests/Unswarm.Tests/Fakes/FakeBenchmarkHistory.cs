using Unswarm.Core.Contracts;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// In-memory IBenchmarkHistory. Newest-first ordering is approximated by insertion
/// order (timestamp is not scriptable here); the real service does the DB ordering.
/// </summary>
public sealed class FakeBenchmarkHistory : IBenchmarkHistory
{
    private readonly List<BenchmarkHistoryEntry> _entries = [];
    private int _seq;

    public List<BenchmarkHistoryEntry> Entries => _entries;
    public List<string> AddedModelIds { get; } = [];

    public Task<BenchmarkHistoryEntry> AddAsync(
        string modelId,
        string? prompt,
        double tokensPerSec,
        double latencyMs,
        long tokensGenerated,
        string status,
        string? errorMessage,
        CancellationToken ct = default,
        string? promptId = null,
        string? promptName = null,
        int? promptVersion = null,
        string? response = null)
    {
        var entry = new BenchmarkHistoryEntry
        {
            Id = $"bh-{Interlocked.Increment(ref _seq)}",
            ModelId = modelId,
            TokensPerSec = tokensPerSec,
            LatencyMs = latencyMs,
            Timestamp = DateTimeOffset.UtcNow,
            Prompt = prompt,
            TokensGenerated = tokensGenerated,
            Status = string.IsNullOrWhiteSpace(status) ? "completed" : status,
            ErrorMessage = errorMessage,
            PromptId = promptId,
            PromptName = promptName,
            PromptVersion = promptVersion,
            Response = response
        };
        lock (_entries) _entries.Add(entry);
        AddedModelIds.Add(modelId);
        return Task.FromResult(entry);
    }

    public Task<IReadOnlyList<BenchmarkHistoryEntry>> ListAsync(int maxCount = 50, string? modelId = null, CancellationToken ct = default)
    {
        lock (_entries)
        {
            var query = _entries.AsEnumerable();
            if (!string.IsNullOrEmpty(modelId))
                query = query.Where(e => e.ModelId == modelId);
            var list = query.Reverse().Take(maxCount).ToList();
            return Task.FromResult<IReadOnlyList<BenchmarkHistoryEntry>>(list);
        }
    }

    public Task<BenchmarkHistoryEntry?> GetLatestForModelAsync(string modelId, CancellationToken ct = default)
    {
        lock (_entries)
        {
            var entry = _entries.LastOrDefault(e => e.ModelId == modelId);
            return Task.FromResult(entry);
        }
    }
}
