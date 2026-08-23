namespace Unswarm.Core.Contracts;

public interface IUsageRecorder
{
    Task RecordAsync(string provider, string model, int promptTokens, int completionTokens, int cachedTokens, bool isStreaming, double? elapsedMs);
}
