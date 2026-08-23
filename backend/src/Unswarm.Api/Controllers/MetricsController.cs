using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Unswarm.Api.Dtos;
using Unswarm.Core.Contracts;
using Unswarm.Core.Persistence;

namespace Unswarm.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class MetricsController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly UnswarmDbContext _db;
    private readonly ISettingsStore _settingsStore;
    private readonly IUsageLiveTailBroadcaster _liveTail;

    public MetricsController(UnswarmDbContext db, ISettingsStore settingsStore, IUsageLiveTailBroadcaster liveTail)
    {
        _db = db;
        _settingsStore = settingsStore;
        _liveTail = liveTail;
    }

    /// <summary>
    /// Returns paginated raw usage records with optional time-range and model/provider filters.
    /// </summary>
    /// <param name="from">Start of time range (ISO 8601). Defaults to 24 hours ago.</param>
    /// <param name="to">End of time range (ISO 8601). Defaults to now.</param>
    /// <param name="since">Cursor filter: only records strictly newer than this timestamp (ISO 8601). Lossless fallback for the /ws/metrics live tail.</param>
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
        [FromQuery] DateTimeOffset? since = null,
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

        // Live-tail cursor: records strictly newer than `since` win over the
        // default window so a poller can replay everything it missed.
        if (since.HasValue)
        {
            var sinceTicks = since.Value.UtcTicks;
            query = query.Where(u => u.TimestampTicks > sinceTicks);
        }

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
                ElapsedMs = u.ElapsedMs,
                ApiKeyName = u.ApiKeyName
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
    /// Live tail of usage records over WebSocket. Each persisted record is pushed
    /// as JSON {id, timestamp, provider, model, promptTokens, completionTokens,
    /// cachedTokens, isStreaming, elapsedMs}. Server pushes only; client messages
    /// are read solely to detect disconnects.
    /// </summary>
    [HttpGet("/ws/metrics")]
    public async Task LiveTail(CancellationToken ct)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = 400;
            return;
        }

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        using var subscription = _liveTail.Subscribe();

        // Client → server traffic is unused; drain it in the background so a
        // close frame (or dropped connection) terminates the push loop promptly.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receiveTask = DrainClientAsync(socket, linkedCts.Token);

        try
        {
            await foreach (var evt in subscription.Reader.ReadAllAsync(linkedCts.Token))
            {
                var json = JsonSerializer.Serialize(evt, JsonOptions);
                var bytes = Encoding.UTF8.GetBytes(json);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, linkedCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path: client disconnected or request aborted.
        }
        finally
        {
            linkedCts.Cancel();
            try { await receiveTask; } catch (OperationCanceledException) { }
        }
    }

    private static async Task DrainClientAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[1024];
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
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
            u.IsStreaming,
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
                    StreamingRequests = g.Count(r => r.IsStreaming),
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
    /// Returns per-model usage breakdown grouped by provider and model, including
    /// latency percentiles (computed in memory — SQLite has no percentile aggregate).
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

        // Percentiles need every ElapsedMs value per group; materialize and
        // aggregate client-side (same pattern as GetSummary's bucketing).
        var records = await query.Select(u => new
        {
            u.Provider,
            u.Model,
            u.PromptTokens,
            u.CompletionTokens,
            u.CachedTokens,
            u.IsStreaming,
            u.ElapsedMs
        }).ToListAsync(ct);

        var models = records
            .GroupBy(r => new { r.Provider, r.Model })
            .Select(g =>
            {
                var latencies = g.Select(r => r.ElapsedMs).OrderBy(x => x).ToList();
                return new ModelUsageSummary
                {
                    Provider = g.Key.Provider,
                    Model = g.Key.Model,
                    RequestCount = g.Count(),
                    StreamingRequests = g.Count(r => r.IsStreaming),
                    PromptTokens = g.Sum(r => (long)r.PromptTokens),
                    CompletionTokens = g.Sum(r => (long)r.CompletionTokens),
                    CachedTokens = g.Sum(r => (long)r.CachedTokens),
                    AvgLatencyMs = latencies.Average(),
                    P50LatencyMs = Percentile(latencies, 50),
                    P95LatencyMs = Percentile(latencies, 95),
                    P99LatencyMs = Percentile(latencies, 99),
                    MaxLatencyMs = latencies.Count > 0 ? latencies[^1] : 0
                };
            })
            .OrderByDescending(m => m.CompletionTokens)
            .ToList();

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
                StreamingRequests = g.Sum(u => u.IsStreaming ? 1 : 0),
                PromptTokens = g.Sum(u => (long)u.PromptTokens),
                CompletionTokens = g.Sum(u => (long)u.CompletionTokens),
                CachedTokens = g.Sum(u => (long)u.CachedTokens)
            })
            .OrderByDescending(p => p.CompletionTokens)
            .ToListAsync(ct);

        return Ok(providers);
    }

    /// <summary>
    /// Returns the union of provider identities usable as filters: distinct
    /// providers seen in usage records (with their recorded kind), configured
    /// cloud providers (kind "cloud"), and registered runtimes (kind "local").
    /// Deduped by name; record-seen entries win over catalog-only ones.
    /// </summary>
    [HttpGet("provider-catalog")]
    [ProducesResponseType(typeof(ProviderCatalogItem[]), 200)]
    public async Task<IActionResult> GetProviderCatalog(CancellationToken ct = default)
    {
        var seen = await _db.UsageRecords
            .GroupBy(u => new { u.Provider, u.ProviderKind })
            .Select(g => new ProviderCatalogItem { Name = g.Key.Provider, Kind = g.Key.ProviderKind })
            .ToListAsync(ct);

        var cloudNames = await _db.CloudProviders.Select(cp => cp.Name).ToListAsync(ct);
        var runtimeNames = await _db.RegisteredRuntimes
            .Select(r => r.DisplayName)
            .ToListAsync(ct);

        var catalog = new List<ProviderCatalogItem>(seen);
        void Upsert(string name, string kind)
        {
            if (string.IsNullOrEmpty(name))
                return;
            if (!catalog.Any(c => c.Name == name))
                catalog.Add(new ProviderCatalogItem { Name = name, Kind = kind });
        }

        foreach (var name in cloudNames)
            Upsert(name, "cloud");
        foreach (var name in runtimeNames)
            Upsert(name, "local");

        return Ok(catalog);
    }

    /// <summary>
    /// Returns per-API-key aggregated usage for requests made with managed keys.
    /// Records without key attribution (cookie-authenticated admins) are excluded.
    /// </summary>
    /// <param name="from">Start of time range (ISO 8601). Defaults to 30 days ago.</param>
    /// <param name="to">End of time range (ISO 8601). Defaults to now.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("api-keys")]
    [ProducesResponseType(typeof(ApiKeyUsageSummary[]), 200)]
    public async Task<IActionResult> GetApiKeyUsage(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var effectiveFrom = from ?? now.AddDays(-30);
        var effectiveTo = to ?? now;

        var fromTicks = effectiveFrom.UtcTicks;
        var toTicks = effectiveTo.UtcTicks;

        var records = await _db.UsageRecords
            .Where(u => u.TimestampTicks >= fromTicks && u.TimestampTicks <= toTicks && u.ApiKeyId != null)
            .Select(u => new
            {
                u.ApiKeyId,
                u.ApiKeyName,
                u.PromptTokens,
                u.CompletionTokens,
                u.CachedTokens,
                u.IsStreaming
            })
            .ToListAsync(ct);

        var keys = records
            .GroupBy(r => r.ApiKeyId)
            .Select(g => new ApiKeyUsageSummary
            {
                ApiKeyId = g.Key!,
                KeyName = g.Select(r => r.ApiKeyName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? string.Empty,
                RequestCount = g.Count(),
                StreamingRequests = g.Count(r => r.IsStreaming),
                PromptTokens = g.Sum(r => (long)r.PromptTokens),
                CompletionTokens = g.Sum(r => (long)r.CompletionTokens),
                CachedTokens = g.Sum(r => (long)r.CachedTokens)
            })
            .OrderByDescending(k => k.CompletionTokens)
            .ToList();

        return Ok(keys);
    }

    /// <summary>
    /// Returns aggregated usage for a single API key over a time range:
    /// overall totals plus a per-(provider, model) breakdown.
    /// </summary>
    /// <param name="keyId">The ApiKeyId stamped on usage records.</param>
    /// <param name="from">Start of time range (ISO 8601). Defaults to 30 days ago.</param>
    /// <param name="to">End of time range (ISO 8601). Defaults to now.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("api-keys/{keyId}/usage")]
    public async Task<IActionResult> GetApiKeyUsageDetail(
        string keyId,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var effectiveFrom = from ?? now.AddDays(-30);
        var effectiveTo = to ?? now;

        var fromTicks = effectiveFrom.UtcTicks;
        var toTicks = effectiveTo.UtcTicks;

        var records = await _db.UsageRecords
            .Where(u => u.ApiKeyId == keyId
                        && u.TimestampTicks >= fromTicks
                        && u.TimestampTicks <= toTicks)
            .Select(u => new
            {
                u.Provider,
                u.Model,
                u.PromptTokens,
                u.CompletionTokens,
                u.CachedTokens
            })
            .ToListAsync(ct);

        return Ok(new KeyUsageResponse
        {
            Totals = new KeyUsageTotals
            {
                RequestCount = records.Count,
                PromptTokens = records.Sum(r => (long)r.PromptTokens),
                CompletionTokens = records.Sum(r => (long)r.CompletionTokens),
                CachedTokens = records.Sum(r => (long)r.CachedTokens)
            },
            Models = records
                .GroupBy(r => new { r.Provider, r.Model })
                .Select(g => new KeyUsageModelRow
                {
                    Provider = g.Key.Provider,
                    Model = g.Key.Model,
                    RequestCount = g.Count(),
                    PromptTokens = g.Sum(r => (long)r.PromptTokens),
                    CompletionTokens = g.Sum(r => (long)r.CompletionTokens),
                    CachedTokens = g.Sum(r => (long)r.CachedTokens)
                })
                .OrderByDescending(m => m.CompletionTokens)
                .ToList()
        });
    }

    /// <summary>
    /// Returns the latency distribution over a time range bucketed into fixed
    /// bands: &lt;500ms, 500ms-1s, 1-2s, 2-5s, 5-10s, &gt;10s.
    /// </summary>
    /// <param name="from">Start of time range (ISO 8601). Defaults to 30 days ago.</param>
    /// <param name="to">End of time range (ISO 8601). Defaults to now.</param>
    /// <param name="provider">Filter by provider name (exact match).</param>
    /// <param name="model">Filter by model name (partial match via Contains).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("latency-bands")]
    [ProducesResponseType(typeof(LatencyBandResponse[]), 200)]
    public async Task<IActionResult> GetLatencyBands(
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

        var latencies = await query.Select(u => u.ElapsedMs).ToListAsync(ct);

        // Upper bounds are inclusive: band i covers (bounds[i-1], bounds[i]].
        long[] bounds = [500, 1000, 2000, 5000, 10000];
        string[] labels = ["<500ms", "500ms-1s", "1-2s", "2-5s", "5-10s", ">10s"];

        var bands = new LatencyBandResponse[labels.Length];
        for (var i = 0; i < labels.Length; i++)
        {
            bands[i] = new LatencyBandResponse
            {
                Label = labels[i],
                MinMs = i == 0 ? 0 : bounds[i - 1],
                MaxMs = i < bounds.Length ? bounds[i] : null,
                Count = 0
            };
        }

        foreach (var ms in latencies)
        {
            var index = Array.FindIndex(bounds, b => ms <= b);
            if (index < 0)
                index = labels.Length - 1;
            bands[index].Count++;
        }

        return Ok(bands);
    }

    /// <summary>
    /// Returns overall aggregated usage totals for a time range, including
    /// latency percentiles (computed in memory — SQLite has no percentile aggregate).
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

        var rows = await query.Select(u => new { u.PromptTokens, u.CompletionTokens, u.CachedTokens, u.IsStreaming, u.ElapsedMs })
            .ToListAsync(ct);

        var latencies = rows.Select(r => r.ElapsedMs).OrderBy(x => x).ToList();

        var totals = new UsageTotalsResponse
        {
            From = effectiveFrom,
            To = effectiveTo,
            TotalRequests = rows.Count,
            TotalStreamingRequests = rows.Count(r => r.IsStreaming),
            TotalPromptTokens = rows.Sum(r => (long)r.PromptTokens),
            TotalCompletionTokens = rows.Sum(r => (long)r.CompletionTokens),
            TotalCachedTokens = rows.Sum(r => (long)r.CachedTokens),
            AvgLatencyMs = latencies.Count > 0 ? latencies.Average() : 0,
            P50LatencyMs = Percentile(latencies, 50),
            P95LatencyMs = Percentile(latencies, 95),
            P99LatencyMs = Percentile(latencies, 99),
            MaxLatencyMs = latencies.Count > 0 ? latencies[^1] : 0
        };

        return Ok(totals);
    }

    /// <summary>
    /// Deletes usage records older than the retention window. Uses the
    /// UsageRetentionDays setting unless an explicit override is supplied.
    /// </summary>
    /// <param name="olderThanDays">Optional override for the retention window in days (0 deletes everything).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("usage/purge")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> PurgeUsage([FromQuery] int? olderThanDays = null, CancellationToken ct = default)
    {
        int days = olderThanDays ?? (await _settingsStore.GetAsync(ct)).UsageRetentionDays;
        if (days < 0)
            days = 0;

        var cutoffTicks = DateTimeOffset.UtcNow.AddDays(-days).UtcTicks;
        var deleted = await _db.UsageRecords
            .Where(u => u.TimestampTicks < cutoffTicks)
            .ExecuteDeleteAsync(ct);

        return Ok(new { deleted });
    }

    /// <summary>
    /// Nearest-rank percentile over an ascending-sorted latency list.
    /// Empty input yields 0.
    /// </summary>
    private static double Percentile(List<long> sortedAsc, int percentile)
    {
        if (sortedAsc.Count == 0)
            return 0;

        var index = (int)Math.Ceiling(percentile / 100.0 * sortedAsc.Count) - 1;
        if (index < 0)
            index = 0;
        return sortedAsc[index];
    }
}
