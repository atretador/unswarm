using Unswarm.Core.Models;

namespace Unswarm.Core.Contracts;

public interface IInferenceProxy
{
    Task<InferenceResponse> InvokeAsync(InferenceRequest request, CancellationToken ct = default);
}
