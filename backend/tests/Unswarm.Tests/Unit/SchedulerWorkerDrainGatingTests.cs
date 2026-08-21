using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Models;
using Unswarm.Core.Services.Scheduler;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Tests for per-target queue semantics: while a streaming response is being
/// consumed, the target's worker must NOT switch models (which would stop the
/// upstream container mid-stream). The next queued request holds its connection
/// until the previous body is fully drained; only then does the switch run.
/// </summary>
public sealed class SchedulerWorkerDrainGatingTests
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
        settings ??= new SchedulerSettings { MaxContainerStartRetries = 1 };
        return new SchedulerWorker(channel, _docker, _inference, _healthChecker, _logStore, _statsTracker, _clock, _logger, settings);
    }

    private static InferenceRequest MakeRequest(string model, string id)
    {
        return new InferenceRequest
        {
            Id = id,
            ModelName = model,
            OriginalJson = "{}",
            Priority = 0,
            EnqueuedAt = DateTimeOffset.UtcNow,
            Tcs = new TaskCompletionSource<InferenceResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
    }

    [Fact]
    public async Task SecondRequest_WaitsUntilFirstStreamDrained_BeforeSwitchingModel()
    {
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var worker = CreateWorker(channel);
        var cts = new CancellationTokenSource();
        worker.Start(cts.Token);

        // Simulates an SSE stream whose body has NOT been fully consumed yet.
        var drainTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstHeadersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _inference.InvokeFunc = (req, ct) =>
        {
            if (req.ModelName == "llama")
            {
                firstHeadersReady.TrySetResult();
                return Task.FromResult(new InferenceResponse
                {
                    StatusCode = 200,
                    TokensGenerated = 5,
                    BodyDrained = drainTcs.Task
                });
            }

            return Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 7 });
        };

        // r1 streams llama; its body is still unconsumed.
        var r1 = MakeRequest("llama", "r1");
        await channel.Writer.WriteAsync(r1);
        await r1.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await firstHeadersReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // r2 wants a different model on the same target: must queue behind r1's drain.
        var r2 = MakeRequest("mistral", "r2");
        await channel.Writer.WriteAsync(r2);

        // Grace period for an incorrect implementation to switch models mid-stream.
        await Task.Delay(300);

        Assert.Empty(_docker.StoppedContainerIds);
        Assert.Equal(["llama"], _docker.StartedModels);
        Assert.False(r2.Tcs.Task.IsCompleted, "r2 must wait while r1's stream is unconsumed");

        // Client finishes consuming r1's body → drain completes → switch proceeds.
        drainTcs.TrySetResult();

        var r2Result = await r2.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, r2Result.StatusCode);
        Assert.Equal(["llama", "mistral"], _docker.StartedModels);
        Assert.Single(_docker.StoppedContainerIds);

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }

    [Fact]
    public async Task NoBodyResponse_DoesNotStallQueue_SwitchRunsImmediately()
    {
        // Buffered responses (BodyDrained null) must not stall the queue:
        // the switch runs as soon as the previous request completes.
        var channel = Channel.CreateUnbounded<InferenceRequest>();
        var worker = CreateWorker(channel);
        var cts = new CancellationTokenSource();
        worker.Start(cts.Token);

        await channel.Writer.WriteAsync(MakeRequest("llama", "r1"));
        var r2 = MakeRequest("mistral", "r2");
        await channel.Writer.WriteAsync(r2);

        var r2Result = await r2.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(200, r2Result.StatusCode);
        Assert.Equal(["llama", "mistral"], _docker.StartedModels);
        Assert.Single(_docker.StoppedContainerIds);

        cts.Cancel();
        await worker.WaitForShutdownAsync();
    }
}
