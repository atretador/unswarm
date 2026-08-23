namespace Unswarm.Api.Dtos;

/// <summary>
/// Single time bucket in a time-series metrics response.
/// </summary>
public sealed class MetricsTimeBucket
{
    public DateTimeOffset BucketStart { get; set; }
    public DateTimeOffset BucketEnd { get; set; }
    public int RequestCount { get; set; }
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
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long CachedTokens { get; set; }
    public double AvgLatencyMs { get; set; }
}

/// <summary>
/// Per-provider usage summary.
/// </summary>
public sealed class ProviderUsageSummary
{
    public string Provider { get; set; } = string.Empty;
    public int RequestCount { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long CachedTokens { get; set; }
}

/// <summary>
/// Overall usage totals over a time range.
/// </summary>
public sealed class UsageTotalsResponse
{
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public int TotalRequests { get; set; }
    public long TotalPromptTokens { get; set; }
    public long TotalCompletionTokens { get; set; }
    public long TotalCachedTokens { get; set; }
    public double AvgLatencyMs { get; set; }
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
}
