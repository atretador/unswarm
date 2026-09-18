using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Persistence;

namespace Unswarm.Core.Services;

/// <summary>
/// Writes inference usage records to the database. Creates its own
/// <see cref="UnswarmDbContext"/> scope so fire-and-forget callers
/// are safe from ObjectDisposedException when the request scope ends.
/// </summary>
public sealed class UsageRecorder : IUsageRecorder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UsageRecorder> _logger;

    public UsageRecorder(IServiceScopeFactory scopeFactory, ILogger<UsageRecorder> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RecordAsync(string provider, string model, int promptTokens, int completionTokens, int cachedTokens, bool isStreaming, double? elapsedMs,
        string? apiKeyId = null, string? apiKeyName = null, string providerKind = "local", string? agent = null)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<UnswarmDbContext>();

            var now = DateTimeOffset.UtcNow;
            var entity = new UsageRecordEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = now,
                TimestampTicks = now.Ticks,
                Provider = provider,
                ProviderKind = providerKind,
                Agent = agent,
                Model = model,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                CachedTokens = cachedTokens,
                IsStreaming = isStreaming,
                ElapsedMs = (long)(elapsedMs ?? 0),
                ApiKeyId = apiKeyId,
                ApiKeyName = apiKeyName
            };

            db.UsageRecords.Add(entity);
            await db.SaveChangesAsync();

            // Cost unit: local usage is attributed to the agent (falling back to
            // the raw display name for legacy rows without an agent); cloud usage
            // keeps the cloud provider name in Provider.
            var costUnit = providerKind == "local" && !string.IsNullOrEmpty(agent)
                ? agent
                : provider;

            // Live-tail fan-out after the record is durably persisted. The
            // broadcaster is a singleton resolved from this throwaway scope;
            // absence (unit-test fakes) must never fail recording.
            scope.ServiceProvider.GetService<IUsageLiveTailBroadcaster>()?.Publish(new UsageLiveTailEvent(
                entity.Id,
                entity.Timestamp,
                costUnit,
                entity.ProviderKind,
                entity.Model,
                entity.PromptTokens,
                entity.CompletionTokens,
                entity.CachedTokens,
                entity.IsStreaming,
                entity.ElapsedMs,
                entity.Agent));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record usage for provider={Provider}, model={Model}", provider, model);
        }
    }
}
