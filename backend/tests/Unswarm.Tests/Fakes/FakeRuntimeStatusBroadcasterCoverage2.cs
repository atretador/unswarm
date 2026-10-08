using Unswarm.Core.Contracts;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// Recording <see cref="IRuntimeStatusBroadcaster"/> for coverage tests: keeps
/// every published event so tests can assert broadcast behaviour without a
/// channel/timing dance.
/// </summary>
public sealed class FakeRuntimeStatusBroadcasterCoverage2 : IRuntimeStatusBroadcaster
{
    private readonly List<RuntimeStatusEvent> _published = [];

    public IReadOnlyList<RuntimeStatusEvent> Published
    {
        get { lock (_published) return _published.ToList(); }
    }

    public void Publish(RuntimeStatusEvent evt)
    {
        lock (_published) _published.Add(evt);
    }

    public IRuntimeStatusSubscription Subscribe() => new Subscription();

    private sealed class Subscription : IRuntimeStatusSubscription
    {
        public System.Threading.Channels.ChannelReader<RuntimeStatusEvent> Reader { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<RuntimeStatusEvent>().Reader;

        public void Dispose() { }
    }
}
