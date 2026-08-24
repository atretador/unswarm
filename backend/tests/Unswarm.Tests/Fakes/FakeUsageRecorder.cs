using Unswarm.Core.Contracts;

namespace Unswarm.Tests.Fakes;

/// <summary>Recorded call to <see cref="IUsageRecorder.RecordAsync"/>.</summary>
public sealed record FakeUsageRecord(
    string Provider,
    string Model,
    int PromptTokens,
    int CompletionTokens,
    int CachedTokens,
    bool IsStreaming,
    double? ElapsedMs,
    string? ApiKeyId,
    string? ApiKeyName,
    string ProviderKind);

/// <summary>Captures usage records in memory for controller tests.</summary>
public sealed class FakeUsageRecorder : IUsageRecorder
{
    public List<FakeUsageRecord> Records { get; } = [];

    public Task RecordAsync(string provider, string model, int promptTokens, int completionTokens, int cachedTokens, bool isStreaming, double? elapsedMs,
        string? apiKeyId = null, string? apiKeyName = null, string providerKind = "local")
    {
        lock (Records)
        {
            Records.Add(new FakeUsageRecord(
                provider, model, promptTokens, completionTokens, cachedTokens,
                isStreaming, elapsedMs, apiKeyId, apiKeyName, providerKind));
        }
        return Task.CompletedTask;
    }
}
