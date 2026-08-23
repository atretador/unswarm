using Unswarm.Core.Contracts;
using Unswarm.Core.Models;

namespace Unswarm.E2ETests.Fakes;

/// <summary>
/// In-memory inference proxy. Returns a canned OpenAI-style JSON body; supports a
/// per-model gate (TaskCompletionSource) so tests can hold requests in flight.
/// </summary>
public sealed class FakeInferenceProxy : IInferenceProxy
{
    private readonly Dictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);

    /// <summary>Requests seen by InvokeAsync (entered, not necessarily returned).</summary>
    public List<InferenceRequest> InvokedRequests { get; } = [];
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>Register a gate: InvokeAsync for this model blocks until Release.</summary>
    public void Gate(string modelName)
    {
        lock (_gates)
        {
            _gates[modelName] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Release(string modelName)
    {
        lock (_gates)
        {
            if (_gates.TryGetValue(modelName, out var tcs))
            {
                _gates.Remove(modelName);
                tcs.TrySetResult();
            }
        }
    }

    public async Task<InferenceResponse> InvokeAsync(InferenceRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _callCount);
        lock (InvokedRequests) InvokedRequests.Add(request);

        Task? gate;
        lock (_gates)
        {
            gate = _gates.TryGetValue(request.ModelName, out var tcs) ? tcs.Task : null;
        }
        if (gate is not null)
            await gate.ConfigureAwait(false);

        var body = System.Text.Encoding.UTF8.GetBytes(
            "{\"id\":\"chatcmpl-e2e\",\"object\":\"chat.completion\",\"model\":\"" + request.ModelName + "\"," +
            "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"Hello from " +
            request.ModelName + "\"},\"finish_reason\":\"stop\"}]," +
            "\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":10,\"total_tokens\":15}}");

        return new InferenceResponse
        {
            StatusCode = 200,
            ContentType = "application/json",
            Body = new MemoryStream(body),
            TokensGenerated = 10,
            ServerPromptTokensPerSec = 100,
            ServerTokensPerSec = 50
        };
    }
}
