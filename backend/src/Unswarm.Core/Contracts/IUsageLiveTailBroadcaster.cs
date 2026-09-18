using System.Threading.Channels;

namespace Unswarm.Core.Contracts;

/// <summary>
/// A single persisted usage record, broadcast to live-tail subscribers
/// (GET /ws/metrics) right after <see cref="IUsageRecorder.RecordAsync"/> commits.
/// <see cref="Provider"/> is the cost unit (agent for local usage, cloud provider
/// name for cloud usage); <see cref="Agent"/> carries the raw local agent when
/// one was recorded.
/// </summary>
public sealed record UsageLiveTailEvent(
    string Id,
    DateTimeOffset Timestamp,
    string Provider,
    string ProviderKind,
    string Model,
    int PromptTokens,
    int CompletionTokens,
    int CachedTokens,
    bool IsStreaming,
    long ElapsedMs,
    string? Agent = null);

/// <summary>
/// In-process fan-out of usage records to live-tail subscribers.
/// </summary>
public interface IUsageLiveTailBroadcaster
{
    /// <summary>Opens a subscription; dispose to unsubscribe. Events are dropped (never back-pressured) if a subscriber falls behind.</summary>
    IUsageLiveTailSubscription Subscribe();

    /// <summary>Publishes an event to all current subscribers. Fire-and-forget safe.</summary>
    void Publish(UsageLiveTailEvent evt);
}

/// <summary>A live-tail subscription: read events from <see cref="Reader"/> until completed.</summary>
public interface IUsageLiveTailSubscription : IDisposable
{
    ChannelReader<UsageLiveTailEvent> Reader { get; }
}
