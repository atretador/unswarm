using System.Threading.Channels;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Core.Services.Scheduler;

/// <summary>
/// Implements ISchedulerQueue by wrapping the bounded Channel and SchedulerWorker.
/// EnqueueAsync writes to the channel and awaits the request's TaskCompletionSource.
/// GetSnapshotAsync returns the current slot, waiting items, recent completed, and active transitions.
/// </summary>
public sealed class SchedulerQueue : ISchedulerQueue
{
    private readonly Channel<InferenceRequest> _channel;
    private readonly SchedulerWorker _worker;

    public SchedulerQueue(Channel<InferenceRequest> channel, SchedulerWorker worker)
    {
        _channel = channel;
        _worker = worker;
    }

    public async Task<InferenceResponse> EnqueueAsync(InferenceRequest request, CancellationToken ct = default)
    {
        // Write to channel — will throw if full (natural backpressure via FullMode=Wait)
        await _channel.Writer.WriteAsync(request, ct).ConfigureAwait(false);

        // Await the result via the request's Tcs
        return await request.Tcs.Task.ConfigureAwait(false);
    }

    public Task<QueueSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var snapshot = _worker.GetSnapshot();
        return Task.FromResult(snapshot);
    }
}
