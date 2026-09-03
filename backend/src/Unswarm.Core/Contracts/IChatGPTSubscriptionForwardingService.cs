namespace Unswarm.Core.Contracts;

public interface IChatGPTSubscriptionForwardingService
{
    /// <summary>
    /// Forward a chat/completions request to the ChatGPT subscription API,
    /// translating the request format and streaming the response back.
    /// </summary>
    Task<Stream> ForwardAsync(
        string modelId,
        string requestBody,
        string requestPath,
        bool isStreaming,
        CancellationToken ct);
}
