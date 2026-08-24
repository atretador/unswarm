namespace Unswarm.Api.Dtos;

/// <summary>
/// Single time bucket in a time-series metrics response.
/// </summary>
public sealed class MetricsTimeBucket
{
    public DateTimeOffset BucketStart { get; set; }
    public DateTimeOffset BucketEnd { get; set; }
    /// <summary>
    /// Group identity when the request used <c>groupBy=provider|model</c>;
    /// null for ungrouped responses.
    /// </summary>
    public string? Group { get; set; }
    public int RequestCount { get; set; }
    public int StreamingRequests { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long CachedTokens { get; set; }
    public double AvgLatencyMs { get; set; }
}

/// <summary>
/// Per-model usage summary.
/// </summary>
public sealed class ModelUsageSummary
{
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int RequestCount { get; set; }
    public int StreamingRequests { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long CachedTokens { get; set; }
    public double AvgLatencyMs { get; set; }
    public double P50LatencyMs { get; set; }
    public double P95LatencyMs { get; set; }
    public double P99LatencyMs { get; set; }
    public long MaxLatencyMs { get; set; }
}

/// <summary>
/// Per-provider usage summary.
/// </summary>
public sealed class ProviderUsageSummary
{
    public string Provider { get; set; } = string.Empty;
    public int RequestCount { get; set; }
    public int StreamingRequests { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long CachedTokens { get; set; }
}

/// <summary>
/// Per-API-key usage summary (same shape as <see cref="ProviderUsageSummary"/> plus key identity).
/// </summary>
public sealed class ApiKeyUsageSummary
{
    public string ApiKeyId { get; set; } = string.Empty;
    public string KeyName { get; set; } = string.Empty;
    public int RequestCount { get; set; }
    public int StreamingRequests { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long CachedTokens { get; set; }
}

/// <summary>
/// One entry of the provider catalog: a name usable as a provider filter plus
/// its kind ("cloud" or "local").
/// </summary>
public sealed class ProviderCatalogItem
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
}

/// <summary>
/// One latency band in the latency-distribution response.
/// MaxMs is null for the open-ended top band ("&gt;10s").
/// </summary>
public sealed class LatencyBandResponse
{
    public string Label { get; set; } = string.Empty;
    public long MinMs { get; set; }
    public long? MaxMs { get; set; }
    public int Count { get; set; }
}

/// <summary>
/// Overall usage totals over a time range.
/// </summary>
public sealed class UsageTotalsResponse
{
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public int TotalRequests { get; set; }
    public int TotalStreamingRequests { get; set; }
    public long TotalPromptTokens { get; set; }
    public long TotalCompletionTokens { get; set; }
    public long TotalCachedTokens { get; set; }
    public double AvgLatencyMs { get; set; }
    public double P50LatencyMs { get; set; }
    public double P95LatencyMs { get; set; }
    public double P99LatencyMs { get; set; }
    public long MaxLatencyMs { get; set; }
}

/// <summary>
/// Per-key aggregated usage totals over a time range.
/// </summary>
public sealed class KeyUsageTotals
{
    public int RequestCount { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long CachedTokens { get; set; }
}

/// <summary>
/// Per-model row of a single key's aggregated usage.
/// </summary>
public sealed class KeyUsageModelRow
{
    public string Model { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public int RequestCount { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long CachedTokens { get; set; }
}

/// <summary>
/// Response for GET /api/metrics/api-keys/{keyId}/usage.
/// </summary>
public sealed class KeyUsageResponse
{
    public KeyUsageTotals Totals { get; set; } = new();
    public List<KeyUsageModelRow> Models { get; set; } = [];
}

/// <summary>
/// A single raw usage record for the detail endpoint.
/// </summary>
public sealed class UsageRecordResponse
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int CachedTokens { get; set; }
    public bool IsStreaming { get; set; }
    public long ElapsedMs { get; set; }
    public string? ApiKeyName { get; set; }
}
