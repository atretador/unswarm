using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Fakes;

public sealed class FakeSchedulerQueue : ISchedulerQueue
{
    public Func<InferenceRequest, CancellationToken, Task<InferenceResponse>>? EnqueueFunc { get; set; }
    public InferenceResponse DefaultResponse { get; set; } = new()
    {
        StatusCode = 200,
        ContentType = "application/json",
        TokensGenerated = 42
    };

    public List<InferenceRequest> EnqueuedRequests { get; } = [];

    public Task<InferenceResponse> EnqueueAsync(InferenceRequest request, CancellationToken ct = default)
    {
        lock (EnqueuedRequests) EnqueuedRequests.Add(request);
        return EnqueueFunc is not null
            ? EnqueueFunc(request, ct)
            : Task.FromResult(DefaultResponse);
    }

    public Task<QueueSnapshot> GetSnapshotAsync(CancellationToken ct = default)
        => Task.FromResult(new QueueSnapshot());
}
