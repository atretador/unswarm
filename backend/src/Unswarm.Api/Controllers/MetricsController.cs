using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Unswarm.Api.Dtos;
using Unswarm.Core.Persistence;

namespace Unswarm.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class MetricsController : ControllerBase
{
    private readonly UnswarmDbContext _db;

    public MetricsController(UnswarmDbContext db) => _db = db;

    /// <summary>
    /// Returns paginated raw usage records with optional time-range and model/provider filters.
    /// </summary>
    /// <param name="from">Start of time range (ISO 8601). Defaults to 24 hours ago.</param>
    /// <param name="to">End of time range (ISO 8601). Defaults to now.</param>
    /// <param name="provider">Filter by provider name (exact match).</param>
    /// <param name="model">Filter by model name (partial match via Contains).</param>
    /// <param name="page">Page number (1-based). Defaults to 1.</param>
    /// <param name="pageSize">Page size (1–200). Defaults to 50.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("usage")]
    [ProducesResponseType(typeof(object), 200)]
    public async Task<IActionResult> GetUsage(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? provider = null,
        [FromQuery] string? model = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var effectiveFrom = from ?? now.AddDays(-1);
        var effectiveTo = to ?? now;
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var fromTicks = effectiveFrom.UtcTicks;
        var toTicks = effectiveTo.UtcTicks;

        IQueryable<UsageRecordEntity> query = _db.UsageRecords
            .Where(u => u.TimestampTicks >= fromTicks && u.TimestampTicks <= toTicks);

        if (!string.IsNullOrEmpty(provider))
        {
            query = query.Where(u => u.Provider == provider);
        }

        if (!string.IsNullOrEmpty(model))
        {
            query = query.Where(u => u.Model.Contains(model));
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(u => u.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new UsageRecordResponse
            {
                Id = u.Id,
                Timestamp = u.Timestamp,
                Provider = u.Provider,
                Model = u.Model,
                PromptTokens = u.PromptTokens,
                CompletionTokens = u.CompletionTokens,
                CachedTokens = u.CachedTokens,
                IsStreaming = u.IsStreaming,
                ElapsedMs = u.ElapsedMs
            })
            .ToListAsync(ct);

        return Ok(new
        {
            items,
            total,
            page,
            pageSize
        });
    }

    /// <summary>
    /// Returns time-bucketed aggregation of usage metrics.
    /// </summary>
    /// <param name="from">Start of time range (ISO 8601). Defaults to 30 days ago.</param>
    /// <param name="to">End of time range (ISO 8601). Defaults to now.</param>
    /// <param name="granularity">Bucket granularity: "hour", "day", "week", "month". Defaults to "day".</param>
    /// <param name="provider">Filter by provider name (exact match).</param>
    /// <param name="model">Filter by model name (partial match via Contains).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(MetricsTimeBucket[]), 200)]
    public async Task<IActionResult> GetSummary(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? provider = null,
        [FromQuery] string? model = null,
        [FromQuery] string granularity = "day",
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var effectiveFrom = from ?? now.AddDays(-30);
        var effectiveTo = to ?? now;

        var fromTicks = effectiveFrom.UtcTicks;
        var toTicks = effectiveTo.UtcTicks;

        // Ticks per bucket based on granularity
        var ticksPerBucket = granularity.ToLowerInvariant() switch
        {
            "hour" => 3600L * 10_000_000,
            "day" => 86400L * 10_000_000,
            "week" => 7L * 86400 * 10_000_000,
            "month" => 30L * 86400 * 10_000_000,
            _ => 86400L * 10_000_000 // default to day
        };

        IQueryable<UsageRecordEntity> query = _db.UsageRecords
            .Where(u => u.TimestampTicks >= fromTicks && u.TimestampTicks <= toTicks);

        if (!string.IsNullOrEmpty(provider))
        {
            query = query.Where(u => u.Provider == provider);
        }

        if (!string.IsNullOrEmpty(model))
        {
            query = query.Where(u => u.Model.Contains(model));
        }

        // Materialize to client for bucketing — SQLite lacks integer division
        // in LINQ that EF Core can translate for this pattern.
        var records = await query.Select(u => new
        {
            u.TimestampTicks,
            u.PromptTokens,
            u.CompletionTokens,
            u.CachedTokens,
            u.ElapsedMs
        }).ToListAsync(ct);

        var buckets = records
            .GroupBy(r => r.TimestampTicks / ticksPerBucket)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var bucketStartTicks = g.Key * ticksPerBucket;
                var count = g.Count();
                return new MetricsTimeBucket
                {
                    BucketStart = new DateTimeOffset(bucketStartTicks, TimeSpan.Zero),
                    BucketEnd = new DateTimeOffset((g.Key + 1) * ticksPerBucket, TimeSpan.Zero),
                    RequestCount = count,
                    PromptTokens = g.Sum(r => (long)r.PromptTokens),
                    CompletionTokens = g.Sum(r => (long)r.CompletionTokens),
                    CachedTokens = g.Sum(r => (long)r.CachedTokens),
                    AvgLatencyMs = count > 0 ? g.Average(r => r.ElapsedMs) : 0
                };
            })
            .ToArray();

        return Ok(buckets);
    }

    /// <summary>
    /// Returns per-model usage breakdown grouped by provider and model.
    /// </summary>
    /// <param name="from">Start of time range (ISO 8601). Defaults to 30 days ago.</param>
    /// <param name="to">End of time range (ISO 8601). Defaults to now.</param>
    /// <param name="provider">Filter by provider name (exact match).</param>
    /// <param name="model">Filter by model name (partial match via Contains).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("models")]
    [ProducesResponseType(typeof(ModelUsageSummary[]), 200)]
    public async Task<IActionResult> GetModels(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? provider = null,
        [FromQuery] string? model = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var effectiveFrom = from ?? now.AddDays(-30);
        var effectiveTo = to ?? now;

        var fromTicks = effectiveFrom.UtcTicks;
        var toTicks = effectiveTo.UtcTicks;

        IQueryable<UsageRecordEntity> query = _db.UsageRecords
            .Where(u => u.TimestampTicks >= fromTicks && u.TimestampTicks <= toTicks);

        if (!string.IsNullOrEmpty(provider))
        {
            query = query.Where(u => u.Provider == provider);
        }

        if (!string.IsNullOrEmpty(model))
        {
            query = query.Where(u => u.Model.Contains(model));
        }

        var models = await query
            .GroupBy(u => new { u.Provider, u.Model })
            .Select(g => new ModelUsageSummary
            {
                Provider = g.Key.Provider,
                Model = g.Key.Model,
                RequestCount = g.Count(),
                PromptTokens = g.Sum(u => (long)u.PromptTokens),
                CompletionTokens = g.Sum(u => (long)u.CompletionTokens),
                CachedTokens = g.Sum(u => (long)u.CachedTokens),
                AvgLatencyMs = g.Average(u => u.ElapsedMs)
            })
            .OrderByDescending(m => m.CompletionTokens)
            .ToListAsync(ct);

        return Ok(models);
    }

    /// <summary>
    /// Returns per-provider usage breakdown.
    /// </summary>
    /// <param name="from">Start of time range (ISO 8601). Defaults to 30 days ago.</param>
    /// <param name="to">End of time range (ISO 8601). Defaults to now.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("providers")]
    [ProducesResponseType(typeof(ProviderUsageSummary[]), 200)]
    public async Task<IActionResult> GetProviders(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var effectiveFrom = from ?? now.AddDays(-30);
        var effectiveTo = to ?? now;

        var fromTicks = effectiveFrom.UtcTicks;
        var toTicks = effectiveTo.UtcTicks;

        var providers = await _db.UsageRecords
            .Where(u => u.TimestampTicks >= fromTicks && u.TimestampTicks <= toTicks)
            .GroupBy(u => u.Provider)
            .Select(g => new ProviderUsageSummary
            {
                Provider = g.Key,
                RequestCount = g.Count(),
                PromptTokens = g.Sum(u => (long)u.PromptTokens),
                CompletionTokens = g.Sum(u => (long)u.CompletionTokens),
                CachedTokens = g.Sum(u => (long)u.CachedTokens)
            })
            .OrderByDescending(p => p.CompletionTokens)
            .ToListAsync(ct);

        return Ok(providers);
    }

    /// <summary>
    /// Returns overall aggregated usage totals for a time range.
    /// </summary>
    /// <param name="from">Start of time range (ISO 8601). Defaults to 30 days ago.</param>
    /// <param name="to">End of time range (ISO 8601). Defaults to now.</param>
    /// <param name="provider">Filter by provider name (exact match).</param>
    /// <param name="model">Filter by model name (partial match via Contains).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("totals")]
    [ProducesResponseType(typeof(UsageTotalsResponse), 200)]
    public async Task<IActionResult> GetTotals(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? provider = null,
        [FromQuery] string? model = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var effectiveFrom = from ?? now.AddDays(-30);
        var effectiveTo = to ?? now;

        var fromTicks = effectiveFrom.UtcTicks;
        var toTicks = effectiveTo.UtcTicks;

        IQueryable<UsageRecordEntity> query = _db.UsageRecords
            .Where(u => u.TimestampTicks >= fromTicks && u.TimestampTicks <= toTicks);

        if (!string.IsNullOrEmpty(provider))
        {
            query = query.Where(u => u.Provider == provider);
        }

        if (!string.IsNullOrEmpty(model))
        {
            query = query.Where(u => u.Model.Contains(model));
        }

        var totals = new UsageTotalsResponse
        {
            From = effectiveFrom,
            To = effectiveTo,
            TotalRequests = await query.CountAsync(ct),
            TotalPromptTokens = await query.SumAsync(u => (long)u.PromptTokens, ct),
            TotalCompletionTokens = await query.SumAsync(u => (long)u.CompletionTokens, ct),
            TotalCachedTokens = await query.SumAsync(u => (long)u.CachedTokens, ct),
            AvgLatencyMs = await query.AnyAsync(ct)
                ? await query.AverageAsync(u => u.ElapsedMs, ct)
                : 0
        };

        return Ok(totals);
    }
}
