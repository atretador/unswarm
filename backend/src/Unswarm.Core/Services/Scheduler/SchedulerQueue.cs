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

        // Link the caller's CancellationToken with the request's own CancellationToken
        // so that client disconnects are observed and the request is cleaned up.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, request.CancellationToken);
        using var timeoutCts = new CancellationTokenSource(Timeout.Infinite);
        using var registration = linkedCts.Token.Register(() => timeoutCts.Cancel());

        var completedTask = await Task.WhenAny(request.Tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token)).ConfigureAwait(false);

        if (completedTask != request.Tcs.Task)
        {
            // Client disconnected — let the worker know this request is abandoned
            request.Tcs.TrySetCanceled(request.CancellationToken);
            throw new OperationCanceledException(request.CancellationToken);
        }

        return await request.Tcs.Task.ConfigureAwait(false);
    }

    public Task<QueueSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var snapshot = _worker.GetSnapshot();
        return Task.FromResult(snapshot);
    }

    public Task<bool> CancelItemAsync(string itemId, CancellationToken ct = default)
    {
        var result = _worker.CancelItem(itemId);
        return Task.FromResult(result);
    }

    public Task<bool> ReleaseConversationHoldsAsync(string targetId, CancellationToken ct = default)
    {
        var result = _worker.ReleaseConversationHolds(targetId);
        return Task.FromResult(result);
    }
}
