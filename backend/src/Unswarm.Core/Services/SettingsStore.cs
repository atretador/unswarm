using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Persistence;

namespace Unswarm.Core.Services;

public sealed class SettingsStore : ISettingsStore
{
    private readonly Func<UnswarmDbContext> _dbFactory;
    private readonly ILogger<SettingsStore> _logger;

    public SettingsStore(Func<UnswarmDbContext> dbFactory, ILogger<SettingsStore> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<Settings> GetAsync(CancellationToken ct = default)
    {
        await using var db = _dbFactory();
        var entity = await db.Settings.FindAsync(["default"], ct).ConfigureAwait(false);
        return entity is null ? new Settings() : MapToSettings(entity);
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

        if (entity.Id == "default" && db.Entry(entity).State == EntityState.Detached)
        {
            db.Settings.Add(entity);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Settings updated");
        return MapToSettings(entity);
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
        AgentDisplayNames = e.AgentDisplayNames
    };
}
