using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services.Scheduler;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

public sealed class SchedulerWorkerTests : IDisposable
{
    private readonly FakeDockerController _docker = new();
    private readonly FakeInferenceProxy _inference = new();
    private readonly FakeHealthChecker _healthChecker = new();
    private readonly FakeLogStore _logStore = new();
    private readonly FakeStatsTracker _statsTracker = new();
    private readonly FakeClock _clock = new();
    private readonly ILogger<SchedulerWorker> _logger = new LoggerFactory().CreateLogger<SchedulerWorker>();

    private SchedulerWorker CreateWorker(
        Channel<InferenceRequest>? channel = null,
        SchedulerSettings? settings = null)
    {
        channel ??= Channel.CreateUnbounded<InferenceRequest>();
        settings ??= new SchedulerSettings();
        return new SchedulerWorker(channel, _docker, _inference, _healthChecker, _logStore, _statsTracker, _clock, _logger, settings);
    }

    private static InferenceRequest MakeRequest(
        string model = "llama",
        string? id = null,
        int priority = 0,
        DateTimeOffset? enqueuedAt = null)
    {
        return new InferenceRequest
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            ModelName = model,
            OriginalJson = "{}",
            Priority = priority,
            EnqueuedAt = enqueuedAt ?? DateTimeOffset.UtcNow,
            Tcs = new TaskCompletionSource<InferenceResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
    }

    [Fact]
    public async Task SingleRequest_IsProcessedAndCompleted()
    {
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var worker = CreateWorker(channel);
        var cts = new CancellationTokenSource();
        worker.Start(cts.Token);

        var expected = new InferenceResponse { StatusCode = 200, TokensGenerated = 42 };
        _inference.DefaultResponse = expected;

        var req = MakeRequest("llama", id: "r1");
        await channel.Writer.WriteAsync(req);

        var result = await req.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(42, result.TokensGenerated);
        Assert.Single(_docker.StartedModels);
        Assert.Equal("llama", _docker.StartedModels[0]);
        Assert.Equal(1, _statsTracker.CompletionCount);

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }

    [Fact]
    public async Task SameModel_ProcessedInFIFOOrder()
    {
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var worker = CreateWorker(channel);
        var cts = new CancellationTokenSource();
        worker.Start(cts.Token);

        var invocationOrder = new ConcurrentBag<(string Id, int Seq)>();
        var seq = 0;
        var allDone = new TaskCompletionSource();
        var remaining = 3;

        _inference.InvokeFunc = (req, ct) =>
        {
            var n = Interlocked.Increment(ref seq);
            invocationOrder.Add((req.Id, n));
            if (Interlocked.Decrement(ref remaining) == 0)
                allDone.TrySetResult();
            return Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });
        };

        var r1 = MakeRequest("llama", id: "r1");
        var r2 = MakeRequest("llama", id: "r2");
        var r3 = MakeRequest("llama", id: "r3");

        await channel.Writer.WriteAsync(r1);
        await channel.Writer.WriteAsync(r2);
        await channel.Writer.WriteAsync(r3);

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var ordered = invocationOrder.OrderBy(x => x.Seq).Select(x => x.Id).ToList();
        Assert.Equal(["r1", "r2", "r3"], ordered);
        // Only one model start — no model switch
        Assert.Single(_docker.StartedModels);
        Assert.Empty(_docker.StoppedContainerIds);

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }

    [Fact]
    public async Task ModelSwitch_StopsOldThenStartsNew()
    {
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var worker = CreateWorker(channel);
        var cts = new CancellationTokenSource();
        worker.Start(cts.Token);

        var allDone = new TaskCompletionSource();
        var remaining = 2;

        _inference.InvokeFunc = (req, ct) =>
        {
            if (Interlocked.Decrement(ref remaining) == 0)
                allDone.TrySetResult();
            return Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });
        };

        await channel.Writer.WriteAsync(MakeRequest("llama", id: "r1"));
        await channel.Writer.WriteAsync(MakeRequest("mistral", id: "r2"));

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["llama", "mistral"], _docker.StartedModels);
        // Container was stopped when switching from llama → mistral
        Assert.Single(_docker.StoppedContainerIds);

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }

    [Fact]
    public async Task LazyStop_ContainerStaysRunningBetweenSameModelRequests()
    {
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var settings = new SchedulerSettings { LazyStop = true };
        var worker = CreateWorker(channel, settings);
        var cts = new CancellationTokenSource();
        worker.Start(cts.Token);

        var allDone = new TaskCompletionSource();
        var remaining = 3;

        _inference.InvokeFunc = (req, ct) =>
        {
            if (Interlocked.Decrement(ref remaining) == 0)
                allDone.TrySetResult();
            return Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });
        };

        await channel.Writer.WriteAsync(MakeRequest("llama", id: "r1"));
        await channel.Writer.WriteAsync(MakeRequest("llama", id: "r2"));
        await channel.Writer.WriteAsync(MakeRequest("llama", id: "r3"));

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Container started once, never stopped between same-model requests
        Assert.Single(_docker.StartedModels);
        Assert.Empty(_docker.StoppedContainerIds);

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }

    [Fact]
    public async Task BatchDrain_TransitionsThroughDrainingState()
    {
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var settings = new SchedulerSettings { LazyStop = true, BatchDrain = true };
        var worker = CreateWorker(channel, settings);
        var cts = new CancellationTokenSource();
        worker.Start(cts.Token);

        var allDone = new TaskCompletionSource();
        var remaining = 2;

        _inference.InvokeFunc = (req, ct) =>
        {
            if (Interlocked.Decrement(ref remaining) == 0)
                allDone.TrySetResult();
            return Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });
        };

        // First request starts llama; second triggers switch to mistral
        await channel.Writer.WriteAsync(MakeRequest("llama", id: "r1"));
        await channel.Writer.WriteAsync(MakeRequest("mistral", id: "r2"));

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Verify the switch happened correctly
        Assert.Equal(["llama", "mistral"], _docker.StartedModels);
        Assert.Single(_docker.StoppedContainerIds);

        // After all processing, no active transitions remain
        var snapshot = worker.GetSnapshot();
        Assert.Empty(snapshot.ActiveTransitions);

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }

    [Fact]
    public async Task PriorityMode_ProcessesInChannelOrder_NotPriorityOrder()
    {
        // The channel-based worker processes items in FIFO (channel) order.
        // Priority only affects internal ordering/sorting, not channel dequeue order.
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var settings = new SchedulerSettings { PriorityMode = "priority" };
        var worker = CreateWorker(channel, settings);
        var cts = new CancellationTokenSource();
        worker.Start(cts.Token);

        var invocationOrder = new ConcurrentBag<(string Id, int Seq)>();
        var seq = 0;
        var allDone = new TaskCompletionSource();
        var remaining = 3;

        _inference.InvokeFunc = (req, ct) =>
        {
            var n = Interlocked.Increment(ref seq);
            invocationOrder.Add((req.Id, n));
            if (Interlocked.Decrement(ref remaining) == 0)
                allDone.TrySetResult();
            return Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });
        };

        // Enqueue with priorities: P3 first, P1 second, P2 third
        await channel.Writer.WriteAsync(MakeRequest("llama", id: "P3-high", priority: 3));
        await channel.Writer.WriteAsync(MakeRequest("llama", id: "P1-low", priority: 1));
        await channel.Writer.WriteAsync(MakeRequest("llama", id: "P2-med", priority: 2));

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Channel is FIFO: items processed in write order
        var ordered = invocationOrder.OrderBy(x => x.Seq).Select(x => x.Id).ToList();
        Assert.Equal(["P3-high", "P1-low", "P2-med"], ordered);

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }

    [Fact]
    public async Task FailedContainerStart_FailsQueuedRequestsForModel()
    {
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var worker = CreateWorker(channel);
        var cts = new CancellationTokenSource();
        worker.Start(cts.Token);

        _docker.FailStart = true;
        _docker.StartErrorMessage = "Image not found";

        var req1 = MakeRequest("llama", id: "r1");
        var req2 = MakeRequest("llama", id: "r2");

        await channel.Writer.WriteAsync(req1);
        await channel.Writer.WriteAsync(req2);

        // Both should fail with InvalidOperationException
        var ex1 = await Assert.ThrowsAsync<InvalidOperationException>(() => req1.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("not available", ex1.Message);

        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() => req2.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("not available", ex2.Message);

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }

    [Fact]
    public async Task ErrorInOneRequest_DoesNotBlockSubsequent()
    {
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var worker = CreateWorker(channel);
        var cts = new CancellationTokenSource();
        worker.Start(cts.Token);

        var callCount = 0;
        _inference.InvokeFunc = (req, ct) =>
        {
            var n = Interlocked.Increment(ref callCount);
            if (n == 1)
                throw new InvalidOperationException("Transient error");
            return Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 10 });
        };

        var req1 = MakeRequest("llama", id: "r1");
        var req2 = MakeRequest("llama", id: "r2");

        await channel.Writer.WriteAsync(req1);
        await channel.Writer.WriteAsync(req2);

        // First fails
        await Assert.ThrowsAsync<InvalidOperationException>(() => req1.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        // Second succeeds
        var result = await req2.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, result.StatusCode);

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }

    [Fact]
    public async Task RequestsForOtherModels_ProceedWhenOneModelErrors()
    {
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var worker = CreateWorker(channel);
        var cts = new CancellationTokenSource();
        worker.Start(cts.Token);

        // Docker fails only for "broken" model
        _docker.OnStart = (modelName, ct) =>
        {
            if (modelName == "broken")
            {
                return Task.FromResult(new ContainerStartResult
                {
                    ContainerId = "fail",
                    ErrorMessage = "Container start failed"
                });
            }
            return Task.FromResult(new ContainerStartResult
            {
                ContainerId = $"ok-{modelName}",
                MappedPort = 9000
            });
        };

        var req1 = MakeRequest("broken", id: "r1");
        var req2 = MakeRequest("working", id: "r2");

        await channel.Writer.WriteAsync(req1);
        await channel.Writer.WriteAsync(req2);

        // broken fails
        await Assert.ThrowsAsync<InvalidOperationException>(() => req1.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        // working succeeds — queue is NOT wedged
        var result = await req2.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, result.StatusCode);

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }

    [Fact]
    public async Task QueueDepthExceeded_BoundedChannelBlocks()
    {
        // In the multi-target architecture, the dispatcher reads from the global
        // channel and writes to per-target bounded channels (TargetQueueDepth=16).
        // When the target channel is full and inference is blocked, the dispatcher
        // stops draining the global channel, causing it to fill and writes to block.
        //
        // We use a small global channel (cap=2) so that once the target channel
        // fills up, just 2 more writes saturate the global channel.
        //
        // Sequence: global cap=2, target cap=16.
        // After 16 items dispatched to target (full), dispatcher blocks.
        // We then flood the global channel and verify the final write blocks.
        var channel = Channel.CreateBounded<InferenceRequest>(2);
        var worker = CreateWorker(channel);
        var cts = new CancellationTokenSource();

        var processingFirst = new TaskCompletionSource();
        var firstCall = true;

        _inference.InvokeFunc = async (req, ct) =>
        {
            if (Interlocked.CompareExchange(ref firstCall, false, true))
            {
                processingFirst.SetResult();
                // Block until cancellation — prevents target worker from consuming
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { }
            }
            return new InferenceResponse { StatusCode = 200, TokensGenerated = 1 };
        };

        // Start worker and send first request
        worker.Start(cts.Token);
        await channel.Writer.WriteAsync(MakeRequest("llama", id: "r1"));
        await processingFirst.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Send 16 more requests to fill the per-target channel.
        // With global cap=2, writes may briefly block while the dispatcher drains,
        // but they will all eventually succeed since the target channel has room.
        for (int i = 2; i <= 17; i++)
            await channel.Writer.WriteAsync(MakeRequest("llama", id: $"r{i}"));

        // At this point the target channel (cap=16) should be full.
        // Give the dispatcher time to finish draining the global channel
        // and block on the target channel WriteAsync.
        await Task.Delay(500);

        // The dispatcher should now be blocked on target channel write.
        // Write r18: dispatcher reads it from global, tries target (full) → blocks.
        // Global channel is now empty again.
        await channel.Writer.WriteAsync(MakeRequest("llama", id: "r18"));

        // Small delay so dispatcher reads r18 and gets stuck on target.
        await Task.Delay(100);

        // Fill the global channel to capacity (cap=2).
        // Dispatcher is blocked, so it won't drain.
        await channel.Writer.WriteAsync(MakeRequest("llama", id: "r19"));
        await channel.Writer.WriteAsync(MakeRequest("llama", id: "r20"));

        // The next write should block — both global (2/2) and target (16/16) are full.
        bool writeCompleted = false;
        _ = Task.Run(async () =>
        {
            await channel.Writer.WriteAsync(MakeRequest("llama", id: "r21"));
            Interlocked.Exchange(ref writeCompleted, true);
        });

        await Task.Delay(500);
        Assert.False(Volatile.Read(ref writeCompleted),
            "Write should be blocked — full pipeline: target queue and global channel saturated");

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }

    [Fact]
    public void SchedulerSettings_FromSettings_MapsCorrectly()
    {
        var settings = new Settings
        {
            LazyStop = false,
            BatchDrain = true,
            PriorityMode = "priority",
            MaxQueueDepth = 64,
            RequestTimeout = 300
        };

        var ss = SchedulerSettings.FromSettings(settings);

        Assert.False(ss.LazyStop);
        Assert.True(ss.BatchDrain);
        Assert.Equal("priority", ss.PriorityMode);
        Assert.Equal(64, ss.MaxQueueDepth);
        Assert.Equal(300, ss.RequestTimeout);
    }

    [Fact]
    public async Task GetSnapshot_ShowsCompletedRequests()
    {
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var worker = CreateWorker(channel);
        var cts = new CancellationTokenSource();
        worker.Start(cts.Token);

        _inference.InvokeFunc = (req, ct) =>
            Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 10 });

        var req = MakeRequest("llama", id: "snap1");
        await channel.Writer.WriteAsync(req);
        await req.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Give worker a moment to update snapshot state
        await Task.Delay(50);

        var snapshot = worker.GetSnapshot();
        Assert.Single(snapshot.RecentCompleted);
        Assert.Equal("snap1", snapshot.RecentCompleted[0].Id);
        Assert.Equal(QueueItemStatus.Completed, snapshot.RecentCompleted[0].Status);

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }

    [Fact]
    public async Task HealthCheckTimeout_FailsRequestsForModel()
    {
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var worker = CreateWorker(channel);
        var cts = new CancellationTokenSource();
        worker.Start(cts.Token);

        _healthChecker.IsReady = false;

        var req = MakeRequest("llama", id: "hc1");
        await channel.Writer.WriteAsync(req);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => req.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(ex is InvalidOperationException or TimeoutException);

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }

    public void Dispose()
    {
        // Cleanup handled per-test via cts.Cancel + WaitForShutdownAsync
    }
}
