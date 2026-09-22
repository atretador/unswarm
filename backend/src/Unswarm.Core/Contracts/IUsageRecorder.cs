namespace Unswarm.Core.Contracts;

public interface IUsageRecorder
{
    /// <summary>
    /// Persists one usage record. <paramref name="provider"/> is the concrete
    /// identity (cloud provider name or serving runtime display name);
    /// <paramref name="providerKind"/> discriminates "cloud" vs "local".
    /// <paramref name="agent"/> is the local execution-target (cost unit) for
    /// local usage; pass null for cloud usage.
    /// </summary>
    Task RecordAsync(string provider, string model, int promptTokens, int completionTokens, int cachedTokens, bool isStreaming, double? elapsedMs,
        string? apiKeyId = null, string? apiKeyName = null, string providerKind = "local", string? agent = null);
}
