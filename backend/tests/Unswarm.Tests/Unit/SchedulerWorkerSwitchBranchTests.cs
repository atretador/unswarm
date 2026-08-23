using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services.Scheduler;
using Unswarm.Tests.Fakes;

namespace Unswarm.Tests.Unit;

/// <summary>
/// SwitchModelLockedAsync failure branches: docker start retry exhaustion with
/// FailAllForModel fan-out, and health-wait failure after a successful start.
/// </summary>
public sealed class SchedulerWorkerSwitchBranchTests : IDisposable
{
    private readonly FakeInferenceProxy _inference = new();
    private readonly FakeHealthChecker _healthChecker = new();
    private readonly FakeLogStore _logStore = new();
    private readonly FakeStatsTracker _statsTracker = new();
    private readonly FakeClock _clock = new();
    private readonly FakeContainerRegistry _containerRegistry = new();
    private readonly ILogger<SchedulerWorker> _logger = new LoggerFactory().CreateLogger<SchedulerWorker>();

    private Channel<InferenceRequest> _channel = Channel.CreateUnbounded<InferenceRequest>();
    private SchedulerWorker? _worker;
    private CancellationTokenSource? _cts;

    private SchedulerWorker CreateWorker(SchedulerSettings? settings = null)
    {
        _channel = Channel.CreateUnbounded<InferenceRequest>();
        settings ??= new SchedulerSettings { MaxContainerStartRetries = 1 };
        var host = new FakeDockerController { IdPrefix = "host" };
        _router = new FakeDockerControllerRouter(new Dictionary<string, IDockerController>
        {
            ["host"] = host
        });
        var resolver = new FakeModelTargetResolver();
        resolver.ResolveFunc = (_, _) => Task.FromResult("host");
        _worker = new SchedulerWorker(
            _channel, host, _inference, _healthChecker,
            _logStore, _statsTracker, _clock, _logger, settings,
            _containerRegistry, _router, resolver);
        _cts = new CancellationTokenSource();
        _worker.Start(_cts.Token);
        return _worker;
    }

    private FakeDockerController HostController => (FakeDockerController)_router!.GetController("host");

    private FakeDockerControllerRouter? _router;

    private async Task EnqueueAsync(params InferenceRequest[] requests)
    {
        foreach (var request in requests)
            await _channel.Writer.WriteAsync(request);
    }

    private async Task ShutdownAsync()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            if (_worker is not null)
                await _worker.WaitForShutdownAsync();
        }
    }

    private static InferenceRequest MakeRequest(string model, string? id = null)
        => new()
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            ModelName = model,
            OriginalJson = "{}",
            Priority = 0,
            EnqueuedAt = DateTimeOffset.UtcNow,
            Tcs = new TaskCompletionSource<InferenceResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
        };

    private async Task RegisterModelAsync(string model, string image)
    {
        await _containerRegistry.CreateAsync(new RegisteredRuntime
        {
            Id = $"reg-{model}",
            DisplayName = image,
            Image = image,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _containerRegistry.AddModelMappingAsync($"reg-{model}", model);
    }

    [Fact]
    public async Task DockerStart_RetriesExhausted_FailsRequestAndQueuedFanOut()
    {
        await RegisterModelAsync("model-broken", "broken-image");
        CreateWorker(new SchedulerSettings { MaxContainerStartRetries = 2 });
        var host = HostController;

        _inference.InvokeFunc = (_, _) => Task.FromResult(new InferenceResponse { StatusCode = 200, TokensGenerated = 1 });
        host.FailStart = true;
        host.StartErrorMessage = "Image not found";

        // Two requests for the failing model: the first drives the retry loop,
        // the second is still WAITING when FailAllForModel fans out.
        var r1 = MakeRequest("model-broken", "r1");
        var r2 = MakeRequest("model-broken", "r2");
        await EnqueueAsync(r1, r2);

        // Retry schedule: attempt 1 fails → 4s backoff → attempt 2 fails → exhausted.
        // The request-facing exception is the generic "not available" message; the
        // detailed retry-exhaustion reason goes to the log store and failed rows.
        var ex1 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => r1.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Contains("not available", ex1.Message);

        // Fan-out: FailAllForModel marks the queued sibling Failed while it waits;
        // when it reaches the lane head it re-runs the switch and exhausts too.
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => r2.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(25)));
        Assert.Contains("not available", ex2.Message);

        // Each request drove its own 2-attempt switch: 4 starts total.
        await Eventually.UntilAsync(() => host.StartedModels.Count >= 4, TimeSpan.FromSeconds(20));
        Assert.All(host.StartedModels, m => Assert.Equal("broken-image", m));

        // Nothing is left processing; both requests are terminal failures.
        await Eventually.UntilAsync(() =>
        {
            var snapshot = _worker!.GetSnapshot();
            return snapshot.Processing.Count == 0
                && snapshot.RecentCompleted.Where(i => i.Status == QueueItemStatus.Failed)
                    .Select(i => i.Id).Distinct().Count() >= 2;
        });

        // Detailed retry-exhaustion reason was logged once per exhausted switch.
        var exhaustionErrors = _logStore.Entries
            .Where(e => e.Level == Unswarm.Core.Models.LogLevel.Error && e.Message.Contains("Container start failed after 2 attempts"))
            .ToList();
        Assert.True(exhaustionErrors.Count >= 2,
            $"Expected ≥2 exhaustion errors, got {exhaustionErrors.Count}: {string.Join(" | ", exhaustionErrors.Select(e => e.Message))}");
        Assert.All(exhaustionErrors, e => Assert.Contains("Image not found", e.Message));

        // Terminal rows exist for both requests with a failure reason recorded
        // (rows may appear once per failure write: the fan-out reason and/or the
        // request-facing "Failed to start container" reason).
        var failedRows = _worker!.GetSnapshot().RecentCompleted.Where(i => i.Status == QueueItemStatus.Failed).ToList();
        Assert.True(failedRows.Select(i => i.Id).Distinct().Count() >= 2,
            $"Expected both requests to be Failed, got: {string.Join(", ", failedRows.Select(i => $"{i.Id}={i.Status}"))}");
        Assert.All(failedRows, i => Assert.False(string.IsNullOrEmpty(i.ErrorMessage)));

        await ShutdownAsync();
    }

    [Fact]
    public async Task HealthWaitFails_AfterContainerStart_RequestFailedAndErrorPersisted()
    {
        await RegisterModelAsync("model-sick", "sick-image");
        CreateWorker();
        var host = HostController;

        _healthChecker.IsReady = false; // WaitForReadyAsync throws TimeoutException

        var req = MakeRequest("model-sick", "r1");
        await EnqueueAsync(req);

        // The container WAS started; the health gate then failed the switch.
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => req.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<TimeoutException>(ex);
        Assert.Contains("Health check timeout", ex.Message);

        await Eventually.UntilAsync(() => host.StartedModels.Count == 1);
        Assert.Equal("sick-image", host.StartedModels[0]);
        Assert.Single(_healthChecker.CheckedPorts);

        // Error state persisted on the queue item.
        await Eventually.UntilAsync(() =>
            _worker!.GetSnapshot().RecentCompleted.Any(i =>
                i.Id == "r1" && i.Status == QueueItemStatus.Failed));
        var failed = _worker!.GetSnapshot().RecentCompleted.First(i => i.Id == "r1");
        Assert.Contains("Health check timeout", failed.ErrorMessage);

        await ShutdownAsync();
    }

    public void Dispose()
    {
    }
}
