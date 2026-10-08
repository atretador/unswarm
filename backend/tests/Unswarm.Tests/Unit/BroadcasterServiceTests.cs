using Unswarm.Core.Contracts;
using Unswarm.Core.Services;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests for the in-process fan-out broadcasters (runtime status + usage live
/// tail): multi-subscriber delivery, unsubscribe on dispose, and the bounded
/// channel's drop-oldest behavior for slow subscribers.
/// </summary>
public sealed class BroadcasterServiceTests
{
    private static RuntimeStatusEvent RuntimeEvent(int i)
        => new("agent", DateTimeOffset.UtcNow, [new ContainerStatusUpdate($"c{i}", "m", "running", i)], []);

    private static UsageLiveTailEvent UsageEvent(int i)
        => new($"e{i}", DateTimeOffset.UtcNow, "provider", "local", "model", i, 0, 0, false, 0);

    [Fact]
    public void RuntimeStatus_Publish_FansOutToAllSubscribers()
    {
        var broadcaster = new RuntimeStatusBroadcaster();
        using var a = broadcaster.Subscribe();
        using var b = broadcaster.Subscribe();

        broadcaster.Publish(RuntimeEvent(1));

        Assert.True(a.Reader.TryRead(out var evtA));
        Assert.True(b.Reader.TryRead(out var evtB));
        Assert.Equal("c1", evtA.Containers[0].Id);
        Assert.Equal("c1", evtB.Containers[0].Id);
    }

    [Fact]
    public void RuntimeStatus_DisposedSubscriber_StopsReceiving()
    {
        var broadcaster = new RuntimeStatusBroadcaster();
        var a = broadcaster.Subscribe();
        a.Dispose();

        broadcaster.Publish(RuntimeEvent(1));

        Assert.False(a.Reader.TryRead(out _));
    }

    [Fact]
    public void RuntimeStatus_SlowSubscriber_DropsOldest()
    {
        var broadcaster = new RuntimeStatusBroadcaster();
        using var subscription = broadcaster.Subscribe();

        for (var i = 0; i < 200; i++)
            broadcaster.Publish(RuntimeEvent(i));

        var received = new List<RuntimeStatusEvent>();
        while (subscription.Reader.TryRead(out var evt))
            received.Add(evt);

        // Bounded at 128 with DropOldest → the newest events are retained.
        Assert.Equal(128, received.Count);
        Assert.Equal("c199", received[^1].Containers[0].Id);
        Assert.DoesNotContain(received, e => e.Containers[0].Id == "c0");
    }

    [Fact]
    public void UsageLiveTail_Publish_FansOutToAllSubscribers()
    {
        var broadcaster = new UsageLiveTailBroadcaster();
        using var a = broadcaster.Subscribe();
        using var b = broadcaster.Subscribe();

        broadcaster.Publish(UsageEvent(1));

        Assert.True(a.Reader.TryRead(out var evtA));
        Assert.True(b.Reader.TryRead(out var evtB));
        Assert.Equal("e1", evtA.Id);
        Assert.Equal("e1", evtB.Id);
    }

    [Fact]
    public void UsageLiveTail_DisposedSubscriber_StopsReceiving()
    {
        var broadcaster = new UsageLiveTailBroadcaster();
        var a = broadcaster.Subscribe();
        a.Dispose();

        broadcaster.Publish(UsageEvent(1));

        Assert.False(a.Reader.TryRead(out _));
    }

    [Fact]
    public void UsageLiveTail_SlowSubscriber_DropsOldest()
    {
        var broadcaster = new UsageLiveTailBroadcaster();
        using var subscription = broadcaster.Subscribe();

        for (var i = 0; i < 200; i++)
            broadcaster.Publish(UsageEvent(i));

        var received = new List<UsageLiveTailEvent>();
        while (subscription.Reader.TryRead(out var evt))
            received.Add(evt);

        Assert.Equal(128, received.Count);
        Assert.Equal("e199", received[^1].Id);
        Assert.DoesNotContain(received, e => e.Id == "e0");
    }
}
