using System.Collections.Concurrent;
using System.Threading.Channels;
using Unswarm.Core.Contracts;

namespace Unswarm.Core.Services;

/// <summary>
/// In-process fan-out of persisted usage records to live-tail subscribers.
/// Each subscriber gets a small bounded channel; when a subscriber falls
/// behind, events are dropped for that subscriber only (live tail semantics —
/// the since-cursor on GET /api/metrics/usage is the lossless fallback).
/// </summary>
public sealed class UsageLiveTailBroadcaster : IUsageLiveTailBroadcaster
{
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();

    public IUsageLiveTailSubscription Subscribe()
    {
        var subscriber = new Subscriber(_subscribers);
        _subscribers[subscriber.Id] = subscriber;
        return subscriber;
    }

    public void Publish(UsageLiveTailEvent evt)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            // TryWrite fails only when the bounded channel is completed —
            // a full channel drops its oldest entry instead, so publishing
            // never blocks or throws for a slow subscriber.
            subscriber.InnerChannel.Writer.TryWrite(evt);
        }
    }

    private sealed class Subscriber : IUsageLiveTailSubscription
    {
        private readonly ConcurrentDictionary<Guid, Subscriber> _registry;

        public Subscriber(ConcurrentDictionary<Guid, Subscriber> registry) => _registry = registry;

        public Guid Id { get; } = Guid.NewGuid();

        public Channel<UsageLiveTailEvent> InnerChannel { get; } =
            Channel.CreateBounded<UsageLiveTailEvent>(new BoundedChannelOptions(128)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });

        public ChannelReader<UsageLiveTailEvent> Reader => InnerChannel.Reader;

        public void Dispose()
        {
            InnerChannel.Writer.TryComplete();
            _registry.TryRemove(Id, out _);
        }
    }
}
