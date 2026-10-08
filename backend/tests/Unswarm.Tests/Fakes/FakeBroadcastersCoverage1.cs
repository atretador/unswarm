using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Unswarm.Core.Contracts;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// Channel-backed <see cref="IUsageLiveTailBroadcaster"/> for the /ws/metrics
/// tests. Publish events, then <see cref="Complete"/> to end the push loop
/// deterministically (no timers).
/// </summary>
public sealed class FakeUsageLiveTailBroadcasterCoverage1 : IUsageLiveTailBroadcaster
{
    private readonly Channel<UsageLiveTailEvent> _channel =
        Channel.CreateUnbounded<UsageLiveTailEvent>();

    public void Publish(UsageLiveTailEvent evt) => _channel.Writer.TryWrite(evt);

    /// <summary>Completes the stream so an active subscription reader finishes.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    public IUsageLiveTailSubscription Subscribe() => new Subscription(_channel.Reader);

    private sealed class Subscription : IUsageLiveTailSubscription
    {
        public Subscription(ChannelReader<UsageLiveTailEvent> reader) => Reader = reader;
        public ChannelReader<UsageLiveTailEvent> Reader { get; }
        public void Dispose() { }
    }
}

/// <summary>
/// Channel-backed <see cref="IRuntimeStatusBroadcaster"/> for the SSE tests.
/// </summary>
public sealed class FakeRuntimeStatusBroadcasterCoverage1 : IRuntimeStatusBroadcaster
{
    private readonly Channel<RuntimeStatusEvent> _channel =
        Channel.CreateUnbounded<RuntimeStatusEvent>();

    public void Publish(RuntimeStatusEvent evt) => _channel.Writer.TryWrite(evt);

    public void Complete() => _channel.Writer.TryComplete();

    public IRuntimeStatusSubscription Subscribe() => new Subscription(_channel.Reader);

    private sealed class Subscription : IRuntimeStatusSubscription
    {
        public Subscription(ChannelReader<RuntimeStatusEvent> reader) => Reader = reader;
        public ChannelReader<RuntimeStatusEvent> Reader { get; }
        public void Dispose() { }
    }
}

/// <summary>
/// Minimal <see cref="IHttpWebSocketFeature"/> that accepts to a supplied
/// <see cref="FakeWebSocket"/>; lets a DefaultHttpContext look like a WS upgrade.
/// </summary>
public sealed class TestWebSocketFeatureCoverage1 : IHttpWebSocketFeature
{
    public TestWebSocketFeatureCoverage1(FakeWebSocket socket) => Socket = socket;

    public FakeWebSocket Socket { get; }

    public bool IsWebSocketRequest => true;

    public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
        => Task.FromResult<WebSocket>(Socket);
}
