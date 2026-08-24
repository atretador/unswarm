using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Persistence;

namespace Unswarm.Api.BackgroundServices;

/// <summary>
/// Prunes logs older than the configured LogRetention hours from SQLite.
/// </summary>
public sealed class LogRetentionService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<LogRetentionService> _logger;

    public LogRetentionService(IServiceProvider services, ILogger<LogRetentionService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LogRetentionService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _services.CreateAsyncScope();
                var settingsStore = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
                var settings = await settingsStore.GetAsync(stoppingToken);

                var retentionHours = Math.Max(settings.LogRetention, 1);
                var cutoff = DateTimeOffset.UtcNow.AddHours(-retentionHours);

                var dbFactory = scope.ServiceProvider.GetRequiredService<Func<UnswarmDbContext>>();
                await using var db = dbFactory();

                // Set-based delete: the cutoff predicate runs entirely in SQL
                // (ExecuteDelete) on the TimestampTicks mirror — SQLite cannot
                // translate DateTimeOffset comparisons in WHERE clauses at all.
                // No ids are materialized.
                var cutoffTicks = cutoff.UtcTicks;
                var deletedCount = await db.Logs
                    .Where(l => l.TimestampTicks < cutoffTicks)
                    .ExecuteDeleteAsync(stoppingToken);

                if (deletedCount > 0)
                {
                    _logger.LogInformation("Pruned {Count} log entries older than {Hours}h", deletedCount, retentionHours);
                }

                // Run every hour
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LogRetentionService error");
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }

        _logger.LogInformation("LogRetentionService stopped");
    }
}
