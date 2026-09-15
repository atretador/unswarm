using System.Collections.Concurrent;
using System.Threading.Channels;
using Unswarm.Core.Contracts;

namespace Unswarm.Core.Services;

/// <summary>
/// In-process fan-out of runtime status changes to SSE subscribers.
/// Each subscriber gets a small bounded channel; when a subscriber falls
/// behind, events are dropped for that subscriber only (live tail semantics).
/// </summary>
public sealed class RuntimeStatusBroadcaster : IRuntimeStatusBroadcaster
{
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();

    public IRuntimeStatusSubscription Subscribe()
    {
        var subscriber = new Subscriber(_subscribers);
        _subscribers[subscriber.Id] = subscriber;
        return subscriber;
    }

    public void Publish(RuntimeStatusEvent evt)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.InnerChannel.Writer.TryWrite(evt);
        }
    }

    private sealed class Subscriber : IRuntimeStatusSubscription
    {
        private readonly ConcurrentDictionary<Guid, Subscriber> _registry;

        public Subscriber(ConcurrentDictionary<Guid, Subscriber> registry) => _registry = registry;

        public Guid Id { get; } = Guid.NewGuid();

        public Channel<RuntimeStatusEvent> InnerChannel { get; } =
            Channel.CreateBounded<RuntimeStatusEvent>(new BoundedChannelOptions(128)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });

        public ChannelReader<RuntimeStatusEvent> Reader => InnerChannel.Reader;

        public void Dispose()
        {
            InnerChannel.Writer.TryComplete();
            _registry.TryRemove(Id, out _);
        }
    }
}
