using Unswarm.Core.Contracts;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// Scriptable <see cref="ICloudForwardingService"/> for the benchmark / inference
/// controller coverage tests. Configurable status, body, and thrown exception.
/// </summary>
public sealed class FakeCloudForwardingCoverage2 : ICloudForwardingService
{
    public int StatusCode { get; set; } = 200;
    public string ContentType { get; set; } = "application/json";

    /// <summary>Response body bytes; null = empty body.</summary>
    public byte[]? BodyBytes { get; set; }

    public Exception? Exception { get; set; }

    public List<(string ModelId, string RequestBody, string RequestPath, bool IsStreaming)> Forwarded { get; } = [];

    public Task<CloudForwardResponse> ForwardAsync(
        string modelId,
        string requestBody,
        string requestPath,
        bool isStreaming,
        CancellationToken ct,
        Dictionary<string, string>? forwardedHeaders = null)
    {
        Forwarded.Add((modelId, requestBody, requestPath, isStreaming));
        if (Exception is not null)
            throw Exception;

        return Task.FromResult(new CloudForwardResponse
        {
            StatusCode = StatusCode,
            ContentType = ContentType,
            Body = BodyBytes is null ? null : new MemoryStream(BodyBytes)
        });
    }
}
