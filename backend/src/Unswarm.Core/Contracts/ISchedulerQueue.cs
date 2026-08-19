using Unswarm.Core.Models;

namespace Unswarm.Core.Contracts;

public interface ISchedulerQueue
{
    Task<InferenceResponse> EnqueueAsync(InferenceRequest request, CancellationToken ct = default);
    Task<QueueSnapshot> GetSnapshotAsync(CancellationToken ct = default);
}
