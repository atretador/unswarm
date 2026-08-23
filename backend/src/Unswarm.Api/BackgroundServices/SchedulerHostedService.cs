using Microsoft.Extensions.Hosting;
using Unswarm.Core.Services.Scheduler;

namespace Unswarm.Api.BackgroundServices;

/// <summary>
/// Wraps SchedulerWorker (from Core) as an IHostedService for ASP.NET Core DI.
/// </summary>
public sealed class SchedulerHostedService : IHostedService
{
    private readonly SchedulerWorker _worker;
    private CancellationTokenSource? _cts;

    public SchedulerHostedService(SchedulerWorker worker) => _worker = worker;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker.Start(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Idempotent + race-safe: the host may invoke StopAsync more than once
        // (e.g. explicit stop followed by disposal); cancelling a disposed CTS
        // would throw ObjectDisposedException and fail host shutdown.
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is not null)
        {
            await cts.CancelAsync();
            await _worker.WaitForShutdownAsync();
            cts.Dispose();
        }
    }
}
