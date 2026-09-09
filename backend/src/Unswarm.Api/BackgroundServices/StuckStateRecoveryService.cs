using Microsoft.EntityFrameworkCore;
using Unswarm.Core.Persistence;

namespace Unswarm.Api.BackgroundServices;

/// <summary>
/// On startup, resets any runtimes left in a Starting state from a previous crash
/// or disconnection during StartScriptAsync(). Without this recovery step the
/// frontend would show "Starting…" forever with no Start/Stop controls.
/// </summary>
public class StuckStateRecoveryService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<StuckStateRecoveryService> _logger;

    public StuckStateRecoveryService(IServiceProvider services, ILogger<StuckStateRecoveryService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for DB to be ready
        await Task.Delay(3000, stoppingToken);

        using var scope = _services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<Func<UnswarmDbContext>>();
        using var db = dbFactory();

        var stuck = await db.RegisteredRuntimes
            .Where(r => r.Status == "Starting")
            .ToListAsync(stoppingToken);

        foreach (var runtime in stuck)
        {
            _logger.LogWarning("Resetting stuck Starting state for runtime {Id} ({Image})",
                runtime.Id, runtime.Image);
            runtime.Status = nameof(Core.Models.ContainerRegistrationStatus.Registered);
            runtime.ErrorMessage = "Reset from stuck Starting state";
            runtime.UpdatedAt = DateTimeOffset.UtcNow;
        }

        if (stuck.Count > 0)
        {
            await db.SaveChangesAsync(stoppingToken);
            _logger.LogInformation("Reset {Count} stuck Starting runtimes", stuck.Count);
        }
    }
}
