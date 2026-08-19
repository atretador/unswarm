using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.Tests.Fakes;

public sealed class FakeInferenceProxy : IInferenceProxy
{
    public Func<InferenceRequest, CancellationToken, Task<InferenceResponse>>? InvokeFunc { get; set; }
    public InferenceResponse DefaultResponse { get; set; } = new() { StatusCode = 200, TokensGenerated = 10 };
    public TimeSpan Delay { get; set; }

    public List<InferenceRequest> InvokedRequests { get; } = [];
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public Task<InferenceResponse> InvokeAsync(InferenceRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _callCount);
        lock (InvokedRequests) InvokedRequests.Add(request);

        if (InvokeFunc != null) return InvokeFunc(request, ct);

        if (Delay > TimeSpan.Zero)
            return SimulateDelay(request, ct);

        return Task.FromResult(DefaultResponse);
    }

    private async Task<InferenceResponse> SimulateDelay(InferenceRequest request, CancellationToken ct)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        return DefaultResponse;
    }
}
