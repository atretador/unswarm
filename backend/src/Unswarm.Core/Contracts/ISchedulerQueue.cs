using Unswarm.Core.Models;

namespace Unswarm.Core.Contracts;

public interface ISchedulerQueue
{
    Task<InferenceResponse> EnqueueAsync(InferenceRequest request, CancellationToken ct = default);
    Task<QueueSnapshot> GetSnapshotAsync(CancellationToken ct = default);
    Task<bool> CancelItemAsync(string itemId, CancellationToken ct = default);

    /// <summary>
    /// Immediately clears all conversation holds on a target ("skip timer").
    /// Returns false when the target id is unknown.
    /// </summary>
    Task<bool> ReleaseConversationHoldsAsync(string targetId, CancellationToken ct = default);
}
