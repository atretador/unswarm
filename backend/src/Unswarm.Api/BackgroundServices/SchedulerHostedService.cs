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
        if (_cts is not null)
        {
            _cts.Cancel();
            await _worker.WaitForShutdownAsync();
            _cts.Dispose();
        }
    }
}
