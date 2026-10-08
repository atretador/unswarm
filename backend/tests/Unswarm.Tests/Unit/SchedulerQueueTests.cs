using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services.Scheduler;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="SchedulerQueue"/>: the channel write + TCS await,
/// client-disconnect cancellation linkage, and the snapshot/cancel/release
/// delegation to the worker. The worker is constructed but never started, so the
/// tests complete the request TCS directly — deterministic and Docker-free.
/// </summary>
public sealed class SchedulerQueueTests
{
    private static InferenceRequest MakeRequest(string id = "req-1", CancellationToken cancellationToken = default)
        => new()
        {
            Id = id,
            ModelName = "llama-3",
            OriginalJson = "{}",
            Priority = 0,
            EnqueuedAt = DateTimeOffset.UtcNow,
            Tcs = new TaskCompletionSource<InferenceResponse>(TaskCreationOptions.RunContinuationsAsynchronously),
            CancellationToken = cancellationToken
        };

    private static (SchedulerQueue Queue, Channel<InferenceRequest> Channel) CreateQueue()
    {
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var worker = new SchedulerWorker(
            channel,
            new FakeDockerController(),
            new FakeInferenceProxy(),
            new FakeHealthChecker(),
            new FakeLogStore(),
            new FakeStatsTracker(),
            new FakeClock(),
            NullLogger<SchedulerWorker>.Instance,
            new SchedulerSettings(),
            Options.Create(new ContainerHostOptions()));
        return (new SchedulerQueue(channel, worker), channel);
    }

    [Fact]
    public async Task EnqueueAsync_WritesToChannel_AndAwaitsTcs()
    {
        var (queue, _) = CreateQueue();
        var request = MakeRequest();

        var task = queue.EnqueueAsync(request);

        // The queue wrote the request and is awaiting the TCS.
        request.Tcs.SetResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 7 });

        var response = await task;
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(7, response.TokensGenerated);
    }

    [Fact]
    public async Task EnqueueAsync_WithCancelledToken_Throws()
    {
        var (queue, _) = CreateQueue();
        var request = MakeRequest();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.EnqueueAsync(request, cts.Token));
    }

    [Fact]
    public async Task EnqueueAsync_RequestTokenAlreadyCancelled_ThrowsAndCancelsTcs()
    {
        var (queue, _) = CreateQueue();
        using var rcts = new CancellationTokenSource();
        rcts.Cancel();
        var request = MakeRequest(cancellationToken: rcts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.EnqueueAsync(request));

        Assert.True(request.Tcs.Task.IsCanceled);
    }

    [Fact]
    public async Task GetSnapshotAsync_DelegatesToWorker()
    {
        var (queue, _) = CreateQueue();

        var snapshot = await queue.GetSnapshotAsync();

        Assert.NotNull(snapshot);
    }

    [Fact]
    public async Task CancelItemAsync_UnknownItem_ReturnsFalse()
    {
        var (queue, _) = CreateQueue();

        Assert.False(await queue.CancelItemAsync("nope"));
    }

    [Fact]
    public async Task ReleaseConversationHoldsAsync_UnknownTarget_ReturnsFalse()
    {
        var (queue, _) = CreateQueue();

        Assert.False(await queue.ReleaseConversationHoldsAsync("agent:ghost"));
    }
}
