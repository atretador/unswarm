using Microsoft.EntityFrameworkCore;
using Unswarm.Core.Contracts;
using Unswarm.Core.Persistence;

namespace Unswarm.Core.Services;

/// <summary>
/// Scoped persistence for benchmark history backed by <see cref="UnswarmDbContext"/>.
/// </summary>
public sealed class BenchmarkHistoryService : IBenchmarkHistory
{
    private readonly Func<UnswarmDbContext> _dbFactory;

    public BenchmarkHistoryService(Func<UnswarmDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<BenchmarkHistoryEntry> AddAsync(
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
        string? response = null,
        string? reasoning = null)
    {
        await using var db = _dbFactory();
        var entity = new BenchmarkHistoryEntity
        {
            Id = Guid.NewGuid().ToString("N"),
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
            Response = response,
            Reasoning = reasoning
        };
        db.Benchmarks.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Map(entity);
    }

    public async Task<IReadOnlyList<BenchmarkHistoryEntry>> ListAsync(int maxCount = 50, string? modelId = null, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        if (maxCount <= 0) maxCount = 50;
        // SQLite cannot ORDER BY DateTimeOffset server-side, so materialize then sort.
        var query = db.Benchmarks.AsQueryable();
        if (!string.IsNullOrEmpty(modelId))
            query = query.Where(b => b.ModelId == modelId);
        var entities = await query
            .Take(1000)
            .ToListAsync(ct).ConfigureAwait(false);
        return entities
            .OrderByDescending(b => b.Timestamp)
            .Take(maxCount)
            .Select(Map)
            .ToList();
    }

    public async Task<BenchmarkHistoryEntry?> GetLatestForModelAsync(string modelId, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.Benchmarks
            .Where(b => b.ModelId == modelId)
            .ToListAsync(ct).ConfigureAwait(false);
        var latest = entity
            .OrderByDescending(b => b.Timestamp)
            .FirstOrDefault();
        return latest is null ? null : Map(latest);
    }

    private static BenchmarkHistoryEntry Map(BenchmarkHistoryEntity e) => new()
    {
        Id = e.Id,
        ModelId = e.ModelId,
        TokensPerSec = e.TokensPerSec,
        LatencyMs = e.LatencyMs,
        Timestamp = e.Timestamp,
        Prompt = e.Prompt,
        TokensGenerated = e.TokensGenerated,
        Status = e.Status,
        ErrorMessage = e.ErrorMessage,
        PromptId = e.PromptId,
        PromptName = e.PromptName,
        PromptVersion = e.PromptVersion,
        Response = e.Response,
        Reasoning = e.Reasoning
    };
}
