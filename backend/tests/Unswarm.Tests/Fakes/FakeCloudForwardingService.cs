using Unswarm.Core.Contracts;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// No-op cloud forwarding service for controller tests. Returns a canned 200
/// JSON body; tests that exercise forwarding behavior should replace this or
/// assert through the controller's response instead.
/// </summary>
public sealed class FakeCloudForwardingService : ICloudForwardingService
{
    /// <summary>Requests captured by <see cref="ForwardAsync"/>, in call order.</summary>
    public List<(string ModelId, string RequestBody, string RequestPath, bool IsStreaming)> Forwarded { get; } = [];

    /// <summary>When non-null, the next forward fails with this status code.</summary>
    public int? FailWithStatusCode { get; set; }

    public Task<CloudForwardResponse> ForwardAsync(
        string modelId,
        string requestBody,
        string requestPath,
        bool isStreaming,
        CancellationToken ct)
    {
        Forwarded.Add((modelId, requestBody, requestPath, isStreaming));
        return Task.FromResult(new CloudForwardResponse
        {
            StatusCode = FailWithStatusCode ?? 200,
            ContentType = "application/json",
            Body = new MemoryStream("\"ok\""u8.ToArray())
        });
    }
}
