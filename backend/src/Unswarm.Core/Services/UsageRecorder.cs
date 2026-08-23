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

    public async Task RecordAsync(string provider, string model, int promptTokens, int completionTokens, int cachedTokens, bool isStreaming, double? elapsedMs)
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
                Model = model,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                CachedTokens = cachedTokens,
                IsStreaming = isStreaming,
                ElapsedMs = (long)(elapsedMs ?? 0)
            };

            db.UsageRecords.Add(entity);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record usage for provider={Provider}, model={Model}", provider, model);
        }
    }
}
