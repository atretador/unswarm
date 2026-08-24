using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;

namespace Unswarm.Core.Services;

/// <summary>
/// Settings store with an in-memory snapshot cache. <see cref="GetAsync"/> serves
/// the cached immutable-by-convention snapshot without touching SQLite; the cache
/// is refreshed eagerly by <see cref="UpdateAsync"/> (the only writer) and has a
/// short TTL fallback so out-of-band DB edits converge too. This removes the
/// per-scheduling-step and per-completion settings reads on the scheduler hot path.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    private readonly Func<UnswarmDbContext> _dbFactory;
    private readonly ILogger<SettingsStore> _logger;

    /// <summary>TTL for the cached snapshot (belt-and-braces re-read window).</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private volatile CachedSettings? _cache;

    private sealed record CachedSettings(Settings Value, DateTimeOffset LoadedAt);

    public SettingsStore(Func<UnswarmDbContext> dbFactory, ILogger<SettingsStore> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<Settings> GetAsync(CancellationToken ct = default)
    {
        var cached = _cache;
        if (cached is not null && DateTimeOffset.UtcNow - cached.LoadedAt < CacheTtl)
            return cached.Value;

        try
        {
            var fresh = await LoadAsync(ct).ConfigureAwait(false);
            _cache = new CachedSettings(fresh, DateTimeOffset.UtcNow);
            return fresh;
        }
        catch (Exception ex)
        {
            // Serve the last known snapshot when a re-read fails.
            if (cached is not null)
            {
                _logger.LogWarning(ex, "Settings re-read failed; serving cached snapshot");
                return cached.Value;
            }

            throw;
        }
    }

    public async Task<Settings> UpdateAsync(Settings settings, CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.Settings.FindAsync(["default"], ct).ConfigureAwait(false)
            ?? new SettingsEntity { Id = "default" };

        entity.RequestTimeout = settings.RequestTimeout;
        entity.HealthCheckInterval = settings.HealthCheckInterval;
        entity.AutoShutdownIdle = settings.AutoShutdownIdle;
        entity.IdleTimeout = settings.IdleTimeout;
        entity.LogRetention = settings.LogRetention;
        entity.EnableBenchmarking = settings.EnableBenchmarking;
        entity.PriorityMode = settings.PriorityMode;
        entity.BatchDrain = settings.BatchDrain;
        entity.LazyStop = settings.LazyStop;
        entity.MaxQueueDepth = settings.MaxQueueDepth;
        entity.MaxConcurrentTargets = settings.MaxConcurrentTargets;
        entity.ParallelSlotSkipLimit = settings.ParallelSlotSkipLimit;
        entity.EnableParallelSlotSkip = settings.EnableParallelSlotSkip;
        entity.QueueStepsTillReset = settings.QueueStepsTillReset;
        entity.EnableConversationAffinity = settings.EnableConversationAffinity;
        entity.ConversationDwellSeconds = settings.ConversationDwellSeconds;
        entity.HideOriginPrefix = settings.HideOriginPrefix;
        entity.AgentDisplayNames = settings.AgentDisplayNames;
        entity.UsageRetentionDays = settings.UsageRetentionDays;
        entity.ProviderBudgetsJson = settings.ProviderBudgetsJson;

        if (entity.Id == "default" && db.Entry(entity).State == EntityState.Detached)
        {
            db.Settings.Add(entity);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Settings updated");

        var updated = MapToSettings(entity);
        _cache = new CachedSettings(updated, DateTimeOffset.UtcNow);
        return updated;
    }

    private async Task<Settings> LoadAsync(CancellationToken ct)
    {
        await using var db = _dbFactory();
        var entity = await db.Settings.FindAsync(["default"], ct).ConfigureAwait(false);
        return entity is null ? new Settings() : MapToSettings(entity);
    }

    private static Settings MapToSettings(SettingsEntity e) => new()
    {
        RequestTimeout = e.RequestTimeout,
        HealthCheckInterval = e.HealthCheckInterval,
        AutoShutdownIdle = e.AutoShutdownIdle,
        IdleTimeout = e.IdleTimeout,
        LogRetention = e.LogRetention,
        EnableBenchmarking = e.EnableBenchmarking,
        PriorityMode = e.PriorityMode,
        BatchDrain = e.BatchDrain,
        LazyStop = e.LazyStop,
        MaxQueueDepth = e.MaxQueueDepth,
        MaxConcurrentTargets = e.MaxConcurrentTargets,
        ParallelSlotSkipLimit = e.ParallelSlotSkipLimit,
        EnableParallelSlotSkip = e.EnableParallelSlotSkip,
        QueueStepsTillReset = e.QueueStepsTillReset,
        EnableConversationAffinity = e.EnableConversationAffinity,
        ConversationDwellSeconds = e.ConversationDwellSeconds,
        HideOriginPrefix = e.HideOriginPrefix,
        AgentDisplayNames = e.AgentDisplayNames,
        UsageRetentionDays = e.UsageRetentionDays,
        ProviderBudgetsJson = e.ProviderBudgetsJson
    };
}
