namespace Unswarm.Core.Contracts;

public interface ICloudForwardingService
{
    /// <summary>
    /// Forward an inference request to a cloud provider. modelId is the full
    /// "cloud/&lt;providerName&gt;/&lt;model&gt;" id. requestBody is the raw JSON body.
    /// Returns the response status code, content type, and a readable stream of the body.
    /// </summary>
    Task<CloudForwardResponse> ForwardAsync(
        string modelId,
        string requestBody,
        string requestPath,
        bool isStreaming,
        CancellationToken ct);
}

public sealed class CloudForwardResponse
{
    public int StatusCode { get; init; }
    public string ContentType { get; init; } = "application/json";
    public Stream? Body { get; init; }
}
