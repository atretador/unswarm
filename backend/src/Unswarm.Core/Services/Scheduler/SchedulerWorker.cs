using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Unswarm.Core.Contracts;
using Unswarm.Core.Models;
using Unswarm.Core.Services.Remote;
using LogLevel = Unswarm.Core.Models.LogLevel;

namespace Unswarm.Core.Services.Scheduler;

/// <summary>
/// Multi-target non-preemptive scheduler. A dispatcher reads requests from a global
/// bounded channel, resolves each request's execution target ("host" | "agent:&lt;name&gt;"),
/// and enqueues it into that target's own bounded channel. Each target runs a sequential
/// worker that preserves single-slot behavior WITHIN the target: it ensures the correct
/// container is running (stop/start scoped to that target only), batch-drains before
/// switching, applies canRunAlongWith compatibility, and fails queued requests for that
/// target when a container cannot be started. Containers on different targets run
/// concurrently.
/// </summary>
public sealed class SchedulerWorker
{
    /// <summary>Default per-target channel capacity (used when store is unavailable).</summary>
    private const int DefaultTargetQueueDepth = 16;

    /// <summary>
    /// Cap on terminal (Completed/Failed) entries retained in <see cref="_allItems"/>.
    /// Recent completions stay queryable via <see cref="_recentCompleted"/>; older
    /// terminal rows are pruned so long-running instances don't grow memory forever.
    /// </summary>
    private const int MaxTerminalTrackedItems = 500;

    private readonly Channel<InferenceRequest> _channel;
    private readonly IDockerController _hostDocker;
    private readonly IDockerControllerRouter _router;
    private readonly IModelTargetResolver _resolver;
    private readonly IInferenceProxy _inference;
    private readonly IHealthChecker _healthChecker;
    private readonly ILogStore _logStore;
    private readonly IStatsTracker _statsTracker;
    private readonly IClock _clock;
    private readonly ILogger<SchedulerWorker> _logger;
    private readonly SchedulerSettings _settings;
    private readonly IContainerRegistry? _containerRegistry;
    private readonly HostScriptRuntimeController? _scriptController;
    private readonly ISettingsStore? _settingsStore;
    private readonly IAgentRegistry? _agentRegistry;

    // Per-target state
    private readonly ConcurrentDictionary<string, TargetSlot> _slots = new(StringComparer.Ordinal);

    // Tracking
    private readonly ConcurrentDictionary<string, QueueItem> _allItems = new();
    // Live request lookup by id — lets drain/fail paths complete the caller's Tcs.
    private readonly ConcurrentDictionary<string, InferenceRequest> _requests = new();
    // FIFO order of terminal item ids, used to prune _allItems beyond the cap.
    private readonly ConcurrentQueue<string> _terminalOrder = new();
    private int _terminalCount;
    private readonly ConcurrentDictionary<string, ModelTransition> _activeTransitions = new();
    private readonly ConcurrentQueue<QueueItem> _recentCompleted = new();
    private readonly object _snapshotLock = new();
    private readonly IDisposable? _queueDepthRegistration;
    private Task? _runTask;

    public SchedulerWorker(
        Channel<InferenceRequest> channel,
        IDockerController docker,
        IInferenceProxy inference,
        IHealthChecker healthChecker,
        ILogStore logStore,
        IStatsTracker statsTracker,
        IClock clock,
        ILogger<SchedulerWorker> logger,
        SchedulerSettings settings,
        IContainerRegistry? containerRegistry = null,
        IDockerControllerRouter? router = null,
        IModelTargetResolver? resolver = null,
        HostScriptRuntimeController? scriptController = null,
        ISettingsStore? settingsStore = null,
        IAgentRegistry? agentRegistry = null)
    {
        _channel = channel;
        _hostDocker = docker;
        _inference = inference;
        _healthChecker = healthChecker;
        _logStore = logStore;
        _statsTracker = statsTracker;
        _clock = clock;
        _logger = logger;
        _settings = settings;
        _containerRegistry = containerRegistry;
        _router = router ?? new HostOnlyDockerControllerRouter(docker);
        _resolver = resolver ?? new HostOnlyTargetResolver();
        _scriptController = scriptController;
        _settingsStore = settingsStore;
        _agentRegistry = agentRegistry;

        // Publish total queue depth (global channel + per-target channels) to the
        // "unswarm.queue.depth" gauge. No-op unless an OTel provider listens to the
        // "Unswarm" meter.
        _queueDepthRegistration = Telemetry.UnswarmMetrics.RegisterQueueDepthProvider(GetTotalQueueDepth);

        // Expose queue depth to the stats tracker so the dashboard shows real values.
        _statsTracker.SetQueueDepthProvider(GetTotalQueueDepth);
    }

    /// <summary>Total requests waiting across the global channel and all target channels.</summary>
    private long GetTotalQueueDepth()
    {
        var depth = (long)_channel.Reader.Count;
        foreach (var slot in _slots.Values)
        {
            depth += slot.Channel.Reader.Count;
        }
        return depth;
    }

    /// <summary>
    /// Returns live settings from the database when an ISettingsStore is available,
    /// otherwise falls back to the injected snapshot. Called once per switch/slot-creation
    /// (not per queued item) to avoid excessive DB reads.
    /// </summary>
    private async Task<SchedulerSettings> GetCurrentSettingsAsync(CancellationToken ct)
    {
        if (_settingsStore is null)
            return _settings;

        try
        {
            var live = await _settingsStore.GetAsync(ct).ConfigureAwait(false);
            return SchedulerSettings.FromSettings(live);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load live settings; falling back to snapshot");
            return _settings;
        }
    }

    /// <summary>Clamps a queue depth value to the valid range [1, 10000].</summary>
    private static int ClampQueueDepth(int value) => Math.Clamp(value, 1, 10000);

    /// <summary>
    /// Resolves the health-check host for a given target. Returns "127.0.0.1" for
    /// host targets or the agent's reported hostname for remote agent targets.
    /// Falls back to the agent name when hostname telemetry hasn't arrived yet.
    /// </summary>
    private string ResolveHealthCheckHost(string targetId)
    {
        var target = ExecutionTarget.FromId(targetId);
        if (!target.IsAgent || target.AgentName is null)
            return "127.0.0.1";

        // Try to get the agent's reported hostname from the registry
        var connection = _agentRegistry?.Get(target.AgentName);
        if (connection is { Hostname: { Length: > 0 } hostname })
            return hostname;

        // Fallback: use the agent name as hostname (works when DNS resolves it)
        return target.AgentName;
    }

    public void Start(CancellationToken ct)
    {
        _runTask = RunAsync(ct);
    }

    public async Task WaitForShutdownAsync()
    {
        if (_runTask is not null)
        {
            try { await _runTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    public QueueSnapshot GetSnapshot()
    {
        lock (_snapshotLock)
        {
            var waiting = _allItems.Values
                .Where(i => i.Status == QueueItemStatus.Waiting)
                .OrderBy(i => i.Priority)
                .ThenBy(i => i.CreatedAt)
                .ToList();

            var current = _allItems.Values
                .FirstOrDefault(i => i.Status == QueueItemStatus.Processing);

            var recent = _recentCompleted.ToArray()
                .OrderByDescending(i => i.CreatedAt)
                .Take(20)
                .ToList();

            var transitions = _activeTransitions.Values
                .Where(t => t.Status != "complete")
                .ToList();

            return new QueueSnapshot
            {
                CurrentSlot = current,
                Waiting = waiting,
                RecentCompleted = recent,
                ActiveTransitions = transitions
            };
        }
    }

    // ── Dispatcher ────────────────────────────────────────────────────────────

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        _logStore.Enqueue(LogLevel.Info, "Scheduler", "Scheduler worker started");

        try
        {
            await foreach (var request in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                var queueItem = CreateQueueItem(request);
                _requests.TryAdd(request.Id, request);
                TryAddItem(queueItem);

                try
                {
                    await DispatchAsync(request, queueItem, stoppingToken).ConfigureAwait(false);

                    // Update queueItem with resolved TargetId now that dispatch has set it
                    if (request.TargetId is not null)
                    {
                        var updatedItem = queueItem with { TargetId = request.TargetId };
                        _allItems[request.Id] = updatedItem;
                        queueItem = updatedItem;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    FailItem(queueItem, "Scheduler shutting down");
                    request.Tcs.TrySetCanceled(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error dispatching request {Id}", request.Id);
                    FailItem(queueItem, ex.Message);
                    request.Tcs.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }

        // Shutdown drain: every item still queued (global channel + per-target
        // channels) must have its Tcs completed so awaiting HTTP handlers return
        // promptly instead of hanging until their client times out.
        DrainQueuedItemsOnShutdown(stoppingToken);

        _logStore.Enqueue(LogLevel.Info, "Scheduler", "Scheduler worker stopped");
    }

    /// <summary>
    /// Completes the Tcs of every request still sitting in the global channel or in
    /// any per-target channel. Best-effort: a slot worker may concurrently dequeue an
    /// item during shutdown, in which case its own cancellation path resolves it.
    /// </summary>
    private void DrainQueuedItemsOnShutdown(CancellationToken stoppingToken)
    {
        while (_channel.Reader.TryRead(out var pending))
            FailPendingOnShutdown(pending, stoppingToken);

        foreach (var slot in _slots.Values)
        {
            while (slot.Channel.Reader.TryRead(out var pending))
                FailPendingOnShutdown(pending, stoppingToken);
        }
    }

    private void FailPendingOnShutdown(InferenceRequest pending, CancellationToken stoppingToken)
    {
        var item = _allItems.TryGetValue(pending.Id, out var existing)
            ? existing
            : CreateQueueItem(pending);
        FailItem(item, "Scheduler shutting down");
        pending.Tcs.TrySetCanceled(stoppingToken);
    }

    private async Task DispatchAsync(InferenceRequest request, QueueItem queueItem, CancellationToken ct)
    {
        var targetId = await _resolver.ResolveTargetAsync(request.ModelName, ct).ConfigureAwait(false);
        request.TargetId = targetId;

        if (!_router.IsTargetReachable(targetId))
        {
            FailItem(queueItem, $"Target {targetId} not reachable for model {request.ModelName}");
            request.Tcs.TrySetException(new InvalidOperationException(
                $"Target {targetId} is not reachable for model {request.ModelName}"));
            return;
        }

        if (_settings.MaxConcurrentTargets > 0
            && !_slots.ContainsKey(targetId)
            && _slots.Count >= _settings.MaxConcurrentTargets)
        {
            FailItem(queueItem, $"Max concurrent targets ({_settings.MaxConcurrentTargets}) exceeded");
            request.Tcs.TrySetException(new InvalidOperationException(
                $"Max concurrent targets ({_settings.MaxConcurrentTargets}) exceeded for model {request.ModelName}"));
            return;
        }

        var slot = await GetOrCreateSlotAsync(targetId, ct).ConfigureAwait(false);
        await slot.Channel.Writer.WriteAsync(request, ct).ConfigureAwait(false);
        EnsureSlotWorkerStarted(slot, ct);
    }

    private async Task<TargetSlot> GetOrCreateSlotAsync(string targetId, CancellationToken ct)
    {
        if (_slots.TryGetValue(targetId, out var existing))
            return existing;

        var currentSettings = await GetCurrentSettingsAsync(ct).ConfigureAwait(false);
        // Existing slots are not resized on settings change — acceptable trade-off.
        var depth = ClampQueueDepth(currentSettings.MaxQueueDepth);

        var slot = _slots.GetOrAdd(targetId, _ => new TargetSlot
        {
            TargetId = targetId,
            Channel = Channel.CreateBounded<InferenceRequest>(new BoundedChannelOptions(depth)
            {
                FullMode = BoundedChannelFullMode.Wait
            }),
            ConcurrencyGate = new SemaphoreSlim(1, 1)
        });

        return slot;
    }

    private void EnsureSlotWorkerStarted(TargetSlot slot, CancellationToken ct)
    {
        lock (slot)
        {
            if (slot.Worker is not null && !slot.Worker.IsCompleted)
                return;

            slot.Worker = RunTargetAsync(slot, ct);
        }
    }

    // ── Per-target worker ─────────────────────────────────────────────────────

    private async Task RunTargetAsync(TargetSlot slot, CancellationToken stoppingToken)
    {
        _logStore.Enqueue(LogLevel.Info, "Scheduler", $"Target worker started for {slot.TargetId}");

        try
        {
            // Manual loop instead of await foreach so we can control whether to
            // fire-and-forget a concurrent request or await sequentially.
            while (await slot.Channel.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                if (!slot.Channel.Reader.TryRead(out var request))
                    continue;

                var queueItem = _allItems.TryGetValue(request.Id, out var existing)
                    ? existing
                    : CreateQueueItem(request);
                _requests.TryAdd(request.Id, request);

                var requestModel = request.ModelName;
                var modelMatches = slot.ResidentModel == requestModel;
                // +1 accounts for the sequential path consuming one slot;
                // with MaxConcurrency=1 this is always false → fully sequential.
                var hasConcurrentCapacity = Volatile.Read(ref slot.ActiveInferences) + 1 < slot.MaxConcurrency;
                var skipsRemaining = _settings.ParallelSlotSkipLimit - Volatile.Read(ref slot.SkipsUsed);

                if (modelMatches && hasConcurrentCapacity && skipsRemaining > 0)
                {
                    // ── Parallel path ──────────────────────────────────────
                    // Same model, capacity available, skip limit not reached.
                    // Launch processing concurrently without awaiting; the next
                    // iteration of the loop reads the next request immediately.
                    Interlocked.Increment(ref slot.SkipsUsed);
                    Interlocked.Increment(ref slot.ActiveInferences);

                    var capturedRequest = request;
                    var capturedQueueItem = queueItem;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ProcessRequestAsync(slot, capturedRequest, capturedQueueItem, stoppingToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            FailItem(capturedQueueItem, "Scheduler shutting down");
                            capturedRequest.Tcs.TrySetCanceled(stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Unexpected error processing request {Id} on target {Target}",
                                capturedRequest.Id, slot.TargetId);
                            FailItem(capturedQueueItem, ex.Message);
                            capturedRequest.Tcs.TrySetException(ex);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref slot.ActiveInferences);
                        }
                    }, stoppingToken);
                }
                else
                {
                    // ── Sequential path ────────────────────────────────────
                    // Different model or limits hit: reset skip counter and
                    // process one-at-a-time, awaiting completion before the
                    // next dequeue.
                    Interlocked.Exchange(ref slot.SkipsUsed, 0);

                    // If the model differs and active inferences are still
                    // running for the previous model, wait for them to drain
                    // before issuing a model switch.
                    if (!modelMatches)
                    {
                        while (Volatile.Read(ref slot.ActiveInferences) > 0)
                        {
                            await Task.Delay(50, stoppingToken).ConfigureAwait(false);
                        }
                    }

                    try
                    {
                        await ProcessRequestAsync(slot, request, queueItem, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        FailItem(queueItem, "Scheduler shutting down");
                        request.Tcs.TrySetCanceled(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unexpected error processing request {Id} on target {Target}",
                            request.Id, slot.TargetId);
                        FailItem(queueItem, ex.Message);
                        request.Tcs.TrySetException(ex);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }

        // Drain any fire-and-forget tasks still running before logging worker stop.
        while (Volatile.Read(ref slot.ActiveInferences) > 0)
        {
            await Task.Delay(50, stoppingToken).ConfigureAwait(false);
        }

        _logStore.Enqueue(LogLevel.Info, "Scheduler", $"Target worker stopped for {slot.TargetId}");
    }

    private async Task ProcessRequestAsync(TargetSlot slot, InferenceRequest request, QueueItem queueItem, CancellationToken ct)
    {
        // Already resolved elsewhere (e.g., cancelled by a batch drain or shutdown):
        // never run inference for it again — the caller is long gone.
        if (request.Tcs.Task.IsCompleted)
            return;

        // Track this request as active for dashboard stats
        _statsTracker.TrackActiveRequest(request.Id);
        try
        {
            await ProcessRequestInnerAsync(slot, request, queueItem, ct).ConfigureAwait(false);
        }
        finally
        {
            _statsTracker.UntrackActiveRequest(request.Id);
        }
    }

    private async Task ProcessRequestInnerAsync(TargetSlot slot, InferenceRequest request, QueueItem queueItem, CancellationToken ct)
    {

        // Ensure correct model is running on this target
        if (slot.ResidentModel != request.ModelName)
        {
            await SwitchModelAsync(slot, request.ModelName, ct).ConfigureAwait(false);
        }

        // If switch failed, the model container won't be running
        if (slot.ResidentModel != request.ModelName)
        {
            FailItem(queueItem, $"Failed to start container for model {request.ModelName}");
            request.Tcs.TrySetException(new InvalidOperationException($"Container for model {request.ModelName} not available"));
            return;
        }

        // Process the request
        UpdateItemStatus(queueItem, QueueItemStatus.Processing);
        _logStore.Enqueue(LogLevel.Info, "Scheduler",
            $"Processing request {request.Id} for model {request.ModelName} on {slot.TargetId}");

        // Declared outside the try so the catch filters can distinguish a request
        // timeout from scheduler shutdown.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.RequestTimeout));

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token, request.CancellationToken);

            var response = await _inference.InvokeAsync(request, linkedCts.Token).ConfigureAwait(false);

            if (response.StatusCode >= 400)
            {
                FailItem(queueItem, $"Inference returned HTTP {response.StatusCode}");
                _statsTracker.RecordError(request);
                request.Tcs.TrySetException(new InvalidOperationException($"Inference returned HTTP {response.StatusCode}"));
                return;
            }

            // Signal the caller that the response headers are ready and the body
            // stream is available. For streaming responses the body is still being
            // consumed by the API controller at this point.
            request.Tcs.TrySetResult(response);

            // ── Stream-drain gating ──────────────────────────────────────────
            // For streaming responses, await full body consumption BEFORE
            // releasing the per-target worker slot. This prevents the next
            // request from dequeuing and triggering a model switch that would
            // kill the upstream container still serving the active stream.
            if (response.BodyDrained is not null)
            {
                try
                {
                    await response.BodyDrained.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Drain cancelled (timeout or upstream gone) — log but do not
                    // fail the request; the client already has the result.
                    _logStore.Enqueue(LogLevel.Warn, "Scheduler",
                        $"Stream drain cancelled for request {request.Id} on {slot.TargetId}");
                }
                catch (Exception ex)
                {
                    // Drain fault (upstream disconnect, etc.) — log and continue.
                    // The request is already completed; this must not propagate.
                    _logStore.Enqueue(LogLevel.Warn, "Scheduler",
                        $"Stream drain faulted for request {request.Id} on {slot.TargetId}: {ex.Message}");
                }

                // Token counts are now final (written by the tap stream on EOF/dispose).
                // Update the queue item with the final token count and timing.
                var finalTokens = response.TokensGenerated;
                var waitMs = (long)(_clock.UtcNow - request.EnqueuedAt).TotalMilliseconds;
                var genTps = response.ServerTokensPerSec > 0
                    ? response.ServerTokensPerSec
                    : (finalTokens > 0 && waitMs > 0 ? finalTokens * 1000.0 / waitMs : 0);
                UpdateItem(queueItem with
                {
                    Status = QueueItemStatus.Completed,
                    TokensGenerated = finalTokens,
                    PromptTokensPerSec = response.ServerPromptTokensPerSec,
                    GenerationTokensPerSec = genTps,
                    ElapsedMs = waitMs,
                    WaitMs = waitMs
                });
            }
            else
            {
                // Buffered (non-streaming) path: response body already consumed.
                var waitMs = (long)(_clock.UtcNow - request.EnqueuedAt).TotalMilliseconds;
                var genTps2 = response.ServerTokensPerSec > 0
                    ? response.ServerTokensPerSec
                    : (response.TokensGenerated > 0 && waitMs > 0 ? response.TokensGenerated * 1000.0 / waitMs : 0);
                UpdateItem(queueItem with
                {
                    Status = QueueItemStatus.Completed,
                    TokensGenerated = response.TokensGenerated,
                    PromptTokensPerSec = response.ServerPromptTokensPerSec,
                    GenerationTokensPerSec = genTps2,
                    ElapsedMs = waitMs,
                    WaitMs = waitMs
                });
            }

            _statsTracker.RecordCompletion(request);

            var totalMs = (long)(_clock.UtcNow - request.EnqueuedAt).TotalMilliseconds;
            _logStore.Enqueue(LogLevel.Info, "Scheduler",
                $"Request {request.Id} completed: {response.TokensGenerated} tokens in {totalMs}ms");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            FailItem(queueItem, "Request timed out");
            request.Tcs.TrySetException(new TimeoutException("Request timed out"));
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
            // Client disconnected — cancel the caller and free the slot.
            FailItem(queueItem, "Client disconnected");
            request.Tcs.TrySetCanceled(request.CancellationToken);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown (the worker may dequeue a buffered item after cancellation
            // before observing the token): cancel the caller, never fake a timeout.
            FailItem(queueItem, "Scheduler shutting down");
            request.Tcs.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inference failed for request {Id}", request.Id);
            FailItem(queueItem, ex.Message);
            _statsTracker.RecordError(request);
            request.Tcs.TrySetException(ex);
        }
    }

    // ── Target-scoped model switching ─────────────────────────────────────────

    private async Task SwitchModelAsync(TargetSlot slot, string targetModel, CancellationToken ct)
    {
        // Safety gate: ensure no active inferences are running before we begin
        // a model switch. RunTargetAsync should already guarantee this, but
        // guard against races if a fire-and-forget task is still winding down.
        while (Volatile.Read(ref slot.ActiveInferences) > 0)
        {
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        var transitionId = Guid.NewGuid().ToString("N");
        var fromModel = slot.ResidentModel ?? "(none)";
        var switchStart = _clock.UtcNow;

        Telemetry.UnswarmMetrics.RecordModelSwitch(fromModel, targetModel, slot.TargetId);

        var transition = new ModelTransition
        {
            Id = transitionId,
            FromModel = fromModel,
            ToModel = targetModel,
            Status = "switching",
            StartedAt = _clock.UtcNow
        };
        _activeTransitions[transitionId] = transition;

        _logStore.Enqueue(LogLevel.Info, "Scheduler",
            $"Switching model on {slot.TargetId}: {fromModel} -> {targetModel}");

        try
        {
            // Resolve the target model's registered container (if any)
            string? targetRegisteredRuntimeId = null;
            RegisteredRuntime? targetRegisteredContainer = null;
            if (_containerRegistry is not null)
            {
                targetRegisteredRuntimeId = await _containerRegistry
                    .GetContainerIdForModelAsync(targetModel, ct).ConfigureAwait(false);

                if (targetRegisteredRuntimeId is not null)
                {
                    targetRegisteredContainer = await _containerRegistry
                        .GetAsync(targetRegisteredRuntimeId, ct).ConfigureAwait(false);
                }
            }

            // Load live settings once per switch for LazyStop / BatchDrain evaluation
            var currentSettings = await GetCurrentSettingsAsync(ct).ConfigureAwait(false);

            // Update concurrency limit from the registered runtime (if available).
            // Safety gate above ensures ActiveInferences == 0, so the gate is not in use.
            if (targetRegisteredContainer is not null)
            {
                var newMax = targetRegisteredContainer.MaxConcurrentInferences;
                slot.MaxConcurrency = newMax;
                var oldGate = slot.ConcurrencyGate;
                slot.ConcurrencyGate = new SemaphoreSlim(newMax, newMax);
                oldGate?.Dispose();
            }

            // Container-aware: same registered container as resident → instant switch
            if (targetRegisteredRuntimeId is not null
                && slot.ResidentRegisteredRuntimeId is not null
                && targetRegisteredRuntimeId == slot.ResidentRegisteredRuntimeId)
            {
                _logStore.Enqueue(LogLevel.Info, "Scheduler",
                    $"Instant switch on {slot.TargetId}: {fromModel} -> {targetModel} (same container {slot.ResidentRegisteredRuntimeId})");

                slot.ResidentModel = targetModel;

                var switchDurationMs = (_clock.UtcNow - switchStart).TotalMilliseconds;
                _statsTracker.RecordSwitch(switchDurationMs);

                transition = transition with { Status = "complete" };
                _activeTransitions[transitionId] = transition;

                _logStore.Enqueue(LogLevel.Info, "Scheduler",
                    $"Instant model switch complete on {slot.TargetId}: now running {targetModel} ({switchDurationMs:F0}ms)");
                return;
            }

            // Target container already running on this target (compatible set) → instant
            if (targetRegisteredRuntimeId is not null
                && slot.RunningContainers.TryGetValue(targetRegisteredRuntimeId, out var alreadyRunning))
            {
                _logStore.Enqueue(LogLevel.Info, "Scheduler",
                    $"Instant switch on {slot.TargetId}: {fromModel} -> {targetModel} (container {alreadyRunning.ContainerName} already running)");

                slot.ResidentModel = targetModel;
                slot.ResidentContainerId = alreadyRunning.ContainerId;
                slot.ResidentRegisteredRuntimeId = targetRegisteredRuntimeId;

                var switchDurationMs2 = (_clock.UtcNow - switchStart).TotalMilliseconds;
                _statsTracker.RecordSwitch(switchDurationMs2);

                transition = transition with { Status = "complete" };
                _activeTransitions[transitionId] = transition;
                return;
            }

            // Drain: if LazyStop, batch-drain all requests for the current model first.
            // When BatchDrain=false, skip the drain and proceed with minimal stop/switch.
            if (currentSettings.LazyStop && currentSettings.BatchDrain
                && slot.ResidentContainerId is not null && slot.ResidentModel is not null)
            {
                transition = transition with { Status = "draining" };
                _activeTransitions[transitionId] = transition;

                await DrainCurrentModelAsync(slot, ct).ConfigureAwait(false);
            }

            // Stop incompatible running containers on this target (canRunAlongWith)
            transition = transition with { Status = "switching" };
            _activeTransitions[transitionId] = transition;

            await StopIncompatibleContainersAsync(slot, targetRegisteredContainer, ct).ConfigureAwait(false);

            // Start new container
            transition = transition with { Status = "starting" };
            _activeTransitions[transitionId] = transition;

            // Script runtime: start via the appropriate controller instead of Docker
            if (targetRegisteredContainer?.RuntimeKind == RuntimeKind.Script)
            {
                var launcherPath = targetRegisteredContainer.LauncherPath
                    ?? throw new InvalidOperationException($"Script runtime {targetRegisteredContainer.Id} has no LauncherPath");

                int scriptPid;
                try
                {
                    var isHost = string.Equals(slot.TargetId, ExecutionTarget.HostId, StringComparison.OrdinalIgnoreCase);
                    if (!isHost)
                    {
                        // Agent-hosted script: start via RemoteAgentDockerController
                        var scriptController = _router.GetController(slot.TargetId);
                        if (scriptController is not RemoteAgentDockerController remoteScriptController)
                        {
                            throw new InvalidOperationException($"Agent target '{slot.TargetId}' does not have a connected RemoteAgentDockerController");
                        }
                        scriptPid = await remoteScriptController.StartScriptAsync(launcherPath, targetRegisteredContainer.ContainerPort, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // Host script: start via HostScriptRuntimeController
                        if (_scriptController is null)
                            throw new InvalidOperationException("HostScriptRuntimeController not available");

                        var scriptResult = await _scriptController.StartScriptAsync(
                            targetRegisteredContainer.Id, launcherPath, targetRegisteredContainer.ContainerPort, ct).ConfigureAwait(false);

                        if (scriptResult.ErrorMessage is not null)
                        {
                            _logStore.Enqueue(LogLevel.Error, "Scheduler",
                                $"Script start failed on {slot.TargetId} for model {targetModel}: {scriptResult.ErrorMessage}");

                            FailAllForModel(targetModel, slot.TargetId, $"Script start failed: {scriptResult.ErrorMessage}");

                            transition = transition with { Status = "complete" };
                            _activeTransitions[transitionId] = transition;
                            return;
                        }

                        scriptPid = scriptResult.Pid ?? 0;
                    }
                }
                catch (Exception ex)
                {
                    _logStore.Enqueue(LogLevel.Error, "Scheduler",
                        $"Script start failed on {slot.TargetId} for model {targetModel}: {ex.Message}");

                    FailAllForModel(targetModel, slot.TargetId, $"Script start failed: {ex.Message}");

                    transition = transition with { Status = "complete" };
                    _activeTransitions[transitionId] = transition;
                    return;
                }

                var scriptKey = $"script:{targetRegisteredContainer.Id}";
                var scriptPort = targetRegisteredContainer.ContainerPort;

                // Wait for health
                var healthHost = ResolveHealthCheckHost(slot.TargetId);
                await _healthChecker.WaitForReadyAsync(scriptPort, healthHost, _settings.HealthCheckTimeoutSeconds, ct).ConfigureAwait(false);

                slot.ResidentModel = targetModel;
                slot.ResidentContainerId = scriptKey;
                slot.ResidentRegisteredRuntimeId = targetRegisteredRuntimeId;

                var scriptRunningKey = targetRegisteredRuntimeId ?? scriptKey;
                slot.RunningContainers[scriptRunningKey] = new RunningContainerInfo
                {
                    Key = scriptRunningKey,
                    RegisteredRuntimeId = targetRegisteredRuntimeId,
                    ContainerName = targetRegisteredContainer.DisplayName ?? targetRegisteredContainer.Image,
                    ContainerId = scriptKey
                };

                var switchDurationScript = (_clock.UtcNow - switchStart).TotalMilliseconds;
                _statsTracker.RecordSwitch(switchDurationScript);

                transition = transition with { Status = "complete" };
                _activeTransitions[transitionId] = transition;

                _logStore.Enqueue(LogLevel.Info, "Scheduler",
                    $"Script switch complete on {slot.TargetId}: now running {targetModel} on port {scriptPort} ({switchDurationScript:F0}ms)");
                return;
            }

            var controller = _router.GetController(slot.TargetId);
            var maxRetries = _settings.MaxContainerStartRetries;
            ContainerStartResult? startResult = null;
            Exception? lastException = null;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (targetRegisteredContainer is not null)
                    {
                        startResult = await controller.StartRegisteredContainerAsync(
                            targetRegisteredContainer.Id,
                            targetRegisteredContainer.Image,
                            targetRegisteredContainer.ContainerPort,
                            targetRegisteredContainer.GpuDevices,
                            targetRegisteredContainer.MemoryLimitMb,
                            targetRegisteredContainer.ExtraLabels ?? new Dictionary<string, string>(),
                            ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // Legacy path: container name matches model name
                        startResult = await controller.StartContainerAsync(targetModel, ct).ConfigureAwait(false);
                    }

                    if (startResult.ErrorMessage is null)
                        break; // Success

                    lastException = new InvalidOperationException(startResult.ErrorMessage);
                    _logStore.Enqueue(LogLevel.Warn, "Scheduler",
                        $"Container start attempt {attempt}/{maxRetries} failed on {slot.TargetId} for model {targetModel}: {startResult.ErrorMessage}");

                    if (attempt < maxRetries)
                    {
                        var delaySec = (int)Math.Pow(2, attempt + 1); // 4s, 8s, 16s
                        _logStore.Enqueue(LogLevel.Info, "Scheduler",
                            $"Retrying container start in {delaySec}s...");
                        await Task.Delay(TimeSpan.FromSeconds(delaySec), ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastException = ex;
                    _logStore.Enqueue(LogLevel.Warn, "Scheduler",
                        $"Container start attempt {attempt}/{maxRetries} threw on {slot.TargetId} for model {targetModel}: {ex.Message}");

                    if (attempt < maxRetries)
                    {
                        var delaySec = (int)Math.Pow(2, attempt + 1);
                        _logStore.Enqueue(LogLevel.Info, "Scheduler",
                            $"Retrying container start in {delaySec}s...");
                        await Task.Delay(TimeSpan.FromSeconds(delaySec), ct).ConfigureAwait(false);
                    }
                }
            }

            if (startResult is null || startResult.ErrorMessage is not null)
            {
                // All retries exhausted
                var errorMsg = lastException?.Message ?? "Unknown error";
                _logStore.Enqueue(LogLevel.Error, "Scheduler",
                    $"Container start failed after {maxRetries} attempts on {slot.TargetId} for model {targetModel}: {errorMsg}");

                FailAllForModel(targetModel, slot.TargetId, $"Container start failed after {maxRetries} attempts: {errorMsg}");

                transition = transition with { Status = "complete" };
                _activeTransitions[transitionId] = transition;
                return;
            }

            // Wait for health
            if (startResult.MappedPort.HasValue)
            {
                var healthHost = ResolveHealthCheckHost(slot.TargetId);
                await _healthChecker.WaitForReadyAsync(startResult.MappedPort.Value, healthHost, _settings.HealthCheckTimeoutSeconds, ct).ConfigureAwait(false);
            }

            slot.ResidentModel = targetModel;
            slot.ResidentContainerId = startResult.ContainerId;
            slot.ResidentRegisteredRuntimeId = targetRegisteredRuntimeId;

            // Persist the live container id so the registry converges when the docker
            // container was recreated (same name, new id) outside this scheduler —
            // otherwise stop/status paths keep operating on the stale id.
            if (_containerRegistry is not null
                && targetRegisteredContainer is not null
                && !string.IsNullOrEmpty(startResult.ContainerId)
                && !string.Equals(startResult.ContainerId, targetRegisteredContainer.RuntimeContainerId, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await _containerRegistry.UpdateAsync(targetRegisteredContainer.Id, targetRegisteredContainer with
                    {
                        RuntimeContainerId = startResult.ContainerId
                    }, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to refresh RuntimeContainerId for registered runtime {RegId} after scheduler start",
                        targetRegisteredContainer.Id);
                }
            }

            var runningKey = targetRegisteredRuntimeId ?? $"legacy:{startResult.ContainerId}";
            slot.RunningContainers[runningKey] = new RunningContainerInfo
            {
                Key = runningKey,
                RegisteredRuntimeId = targetRegisteredRuntimeId,
                ContainerName = targetRegisteredContainer?.Image ?? targetModel,
                ContainerId = startResult.ContainerId
            };

            var switchDurationMs3 = (_clock.UtcNow - switchStart).TotalMilliseconds;
            _statsTracker.RecordSwitch(switchDurationMs3);

            transition = transition with { Status = "complete" };
            _activeTransitions[transitionId] = transition;

            _logStore.Enqueue(LogLevel.Info, "Scheduler",
                $"Model switch complete on {slot.TargetId}: now running {targetModel} on port {startResult.MappedPort} ({switchDurationMs3:F0}ms)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model switch failed on {Target}: {From} -> {To}", slot.TargetId, fromModel, targetModel);

            // Fail all queued requests for the target model
            FailAllForModel(targetModel, slot.TargetId, $"Model switch failed: {ex.Message}");

            transition = transition with { Status = "complete" };
            _activeTransitions[transitionId] = transition;

            throw;
        }
    }

    /// <summary>
    /// Stops containers on this target that cannot run alongside the target container.
    /// With no registry info (legacy path) or an empty canRunAlongWith set, behaves as
    /// single-container mode: every other running container on the target is stopped.
    /// </summary>
    private async Task StopIncompatibleContainersAsync(TargetSlot slot, RegisteredRuntime? targetContainer, CancellationToken ct)
    {
        // No registry or no mapping for the target model → conservative single-slot behavior
        if (_containerRegistry is null || targetContainer is null)
        {
            await StopAllRunningAsync(slot, ct).ConfigureAwait(false);
            return;
        }

        var targetNames = ContainerNames(targetContainer);
        var targetCanRun = new HashSet<string>(targetContainer.CanRunAlongWith ?? [], StringComparer.OrdinalIgnoreCase);

        var toStop = new List<string>();
        foreach (var kv in slot.RunningContainers)
        {
            var info = kv.Value;

            // Same registered container → never stop
            if (info.RegisteredRuntimeId is not null && info.RegisteredRuntimeId == targetContainer.Id)
                continue;

            // Legacy running container (no registry info) → cannot prove compatibility → stop
            if (info.RegisteredRuntimeId is null)
            {
                toStop.Add(kv.Key);
                continue;
            }

            var runningEntity = await _containerRegistry.GetAsync(info.RegisteredRuntimeId, ct).ConfigureAwait(false);
            if (runningEntity is null)
            {
                toStop.Add(kv.Key);
                continue;
            }

            var runningNames = ContainerNames(runningEntity);
            var runningCanRun = new HashSet<string>(runningEntity.CanRunAlongWith ?? [], StringComparer.OrdinalIgnoreCase);

            // Symmetric compatibility: target accepts running, running accepts target
            var targetAccepts = runningNames.Any(runningName => targetCanRun.Contains(runningName));
            var runningAccepts = targetNames.Any(targetName => runningCanRun.Contains(targetName));

            if (!(targetAccepts && runningAccepts))
                toStop.Add(kv.Key);
        }

        var controller = _router.GetController(slot.TargetId);
        foreach (var key in toStop)
        {
            var info = slot.RunningContainers[key];

            // Registered-only stop guard: skip containers not known to the fleet
            if (!await IsFleetRegisteredAsync(info, ct).ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "Skipping stop of unregistered container {ContainerId} on {Target} (not fleet-registered)",
                    info.ContainerId, slot.TargetId);
                continue;
            }

            _logStore.Enqueue(LogLevel.Info, "Scheduler",
                $"Stopping incompatible container {info.ContainerName} on {slot.TargetId}");

            // Script runtimes: dispatch stop to the correct controller
            if (info.ContainerId.StartsWith("script:", StringComparison.Ordinal)
                && info.RegisteredRuntimeId is not null)
            {
                await StopScriptRuntimeAsync(slot.TargetId, info.RegisteredRuntimeId, ct).ConfigureAwait(false);
            }
            else
            {
                var stopTargetId = await ResolveStopTargetIdAsync(controller, info, ct).ConfigureAwait(false);
                await controller.StopContainerAsync(stopTargetId, ct).ConfigureAwait(false);
            }

            slot.RunningContainers.Remove(key);

            if (slot.ResidentContainerId == info.ContainerId)
            {
                slot.ResidentModel = null;
                slot.ResidentContainerId = null;
                slot.ResidentRegisteredRuntimeId = null;
            }
        }
    }

    private async Task StopAllRunningAsync(TargetSlot slot, CancellationToken ct)
    {
        var controller = _router.GetController(slot.TargetId);
        foreach (var kv in slot.RunningContainers.ToList())
        {
            _logStore.Enqueue(LogLevel.Info, "Scheduler",
                $"Stopping container {kv.Value.ContainerName} on {slot.TargetId}");

            // Script runtimes: dispatch stop to the correct controller
            if (kv.Value.ContainerId.StartsWith("script:", StringComparison.Ordinal)
                && kv.Value.RegisteredRuntimeId is not null)
            {
                await StopScriptRuntimeAsync(slot.TargetId, kv.Value.RegisteredRuntimeId, ct).ConfigureAwait(false);
            }
            else
            {
                var stopTargetId = await ResolveStopTargetIdAsync(controller, kv.Value, ct).ConfigureAwait(false);
                await controller.StopContainerAsync(stopTargetId, ct).ConfigureAwait(false);
            }

            slot.RunningContainers.Remove(kv.Key);
        }

        slot.ResidentModel = null;
        slot.ResidentContainerId = null;
        slot.ResidentRegisteredRuntimeId = null;
    }

    /// <summary>
    /// Checks whether a container is fleet-registered and therefore safe to stop.
    /// A container is considered fleet-registered if it has a RegisteredRuntimeId
    /// (scheduler-tracked), or if the IContainerRegistry knows its RuntimeContainerId.
    /// When _containerRegistry is null (legacy tests), all containers are considered
    /// safe to stop to preserve old behavior.
    /// </summary>
    private async Task<bool> IsFleetRegisteredAsync(RunningContainerInfo info, CancellationToken ct)
    {
        // Scheduler-tracked container — always safe to stop
        if (!string.IsNullOrEmpty(info.RegisteredRuntimeId))
            return true;

        // No registry available — legacy behavior: allow stop
        if (_containerRegistry is null)
            return true;

        // Check if any registered runtime claims this container id
        try
        {
            var allRuntimes = await _containerRegistry.ListAllAsync(ct).ConfigureAwait(false);
            if (allRuntimes.Any(r => r.RuntimeContainerId == info.ContainerId))
                return true;
        }
        catch
        {
            // On error, fall through to skip (conservative)
        }

        return false;
    }

    private static IEnumerable<string> ContainerNames(RegisteredRuntime container)
    {
        yield return container.Image;
        if (!string.IsNullOrEmpty(container.DisplayName))
            yield return container.DisplayName;
    }

    /// <summary>
    /// Resolves the container id to stop for a scheduler-tracked running entry.
    /// The cached <see cref="RunningContainerInfo.ContainerId"/> may be stale when the
    /// user recreated the docker container (same name, new id) outside the scheduler.
    /// Re-lists the target's containers and prefers a live match by cached id or by
    /// container name (same matching used by discovery/status); falls back to the cached
    /// id when nothing matches or listing fails (the controller then logs a clear warning).
    /// </summary>
    private static async Task<string> ResolveStopTargetIdAsync(
        IDockerController controller,
        RunningContainerInfo info,
        CancellationToken ct)
    {
        try
        {
            var containers = await controller.ListContainersAsync(ct).ConfigureAwait(false);
            foreach (var c in containers)
            {
                if (!string.IsNullOrEmpty(info.ContainerId) &&
                    string.Equals(c.Id, info.ContainerId, StringComparison.OrdinalIgnoreCase))
                    return c.Id;

                if (!string.IsNullOrEmpty(info.ContainerName) &&
                    (string.Equals(c.ModelName, info.ContainerName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(c.ModelId, info.ContainerName, StringComparison.OrdinalIgnoreCase)))
                    return c.Id;
            }
        }
        catch
        {
            // Listing failed — fall back to the cached id below.
        }

        return info.ContainerId;
    }

    /// <summary>
    /// Stops a script runtime using the appropriate controller for its target.
    /// Host targets use HostScriptRuntimeController (by registration id).
    /// Agent targets use RemoteAgentDockerController.StopScriptAsync (by PID).
    /// </summary>
    private async Task StopScriptRuntimeAsync(string targetId, string registeredRuntimeId, CancellationToken ct)
    {
        var isHost = string.Equals(targetId, ExecutionTarget.HostId, StringComparison.OrdinalIgnoreCase);

        if (isHost)
        {
            if (_scriptController is not null)
            {
                await _scriptController.StopScriptAsync(registeredRuntimeId, ct).ConfigureAwait(false);
            }
            return;
        }

        // Agent target: look up the registered runtime to get the PID, then stop via RemoteAgentDockerController
        if (_containerRegistry is null)
        {
            _logger.LogWarning("Cannot stop agent script {RegId}: no container registry available", registeredRuntimeId);
            return;
        }

        var runtime = await _containerRegistry.GetAsync(registeredRuntimeId, ct).ConfigureAwait(false);
        if (runtime is null || !runtime.RuntimeProcessId.HasValue)
        {
            _logger.LogWarning("Cannot stop agent script {RegId}: runtime not found or has no PID", registeredRuntimeId);
            return;
        }

        var controller = _router.GetController(targetId);
        if (controller is RemoteAgentDockerController remoteController)
        {
            await remoteController.StopScriptAsync(runtime.RuntimeProcessId.Value, ct).ConfigureAwait(false);
        }
        else
        {
            _logger.LogWarning("Cannot stop agent script {RegId}: controller for target {Target} is not a RemoteAgentDockerController",
                registeredRuntimeId, targetId);
        }
    }

    /// <summary>
    /// Batch-drain of the resident model before a switch: every request still
    /// WAITING for that model is failed/cancelled via its Tcs so the awaiting
    /// handler returns immediately. Waiting items are NEVER flipped to Processing
    /// here — they are not being processed, and pretending otherwise leaves
    /// callers hanging on a Tcs that will only resolve when the item is dequeued
    /// again after the switch (if ever).
    /// </summary>
    private Task DrainCurrentModelAsync(TargetSlot slot, CancellationToken ct)
    {
        if (slot.ResidentModel is null) return Task.CompletedTask;

        // Collect all waiting requests for the current model on this target
        var toDrain = _allItems.Values
            .Where(i => i.Status == QueueItemStatus.Waiting && i.ModelRequested == slot.ResidentModel && i.TargetId == slot.TargetId)
            .OrderBy(i => i.Priority)
            .ThenBy(i => i.CreatedAt)
            .ToList();

        if (toDrain.Count == 0) return Task.CompletedTask;

        _logStore.Enqueue(LogLevel.Info, "Scheduler",
            $"Batch-draining {toDrain.Count} requests for model {slot.ResidentModel} on {slot.TargetId}");

        foreach (var item in toDrain)
        {
            ct.ThrowIfCancellationRequested();
            FailItem(item, "Cancelled during batch-drain before model switch");
            if (_requests.TryGetValue(item.Id, out var request))
                request.Tcs.TrySetCanceled(ct);
        }

        return Task.CompletedTask;
    }

    private void FailAllForModel(string modelName, string targetId, string errorMessage)
    {
        var toFail = _allItems.Values
            .Where(i => i.Status == QueueItemStatus.Waiting && i.ModelRequested == modelName && i.TargetId == targetId)
            .ToList();

        foreach (var item in toFail)
        {
            FailItem(item, errorMessage);
        }
    }

    // ── Queue item helpers ────────────────────────────────────────────────────

    private QueueItem CreateQueueItem(InferenceRequest request)
    {
        return new QueueItem
        {
            Id = request.Id,
            ModelRequested = request.ModelName,
            TargetId = request.TargetId,
            Status = QueueItemStatus.Waiting,
            Priority = request.Priority,
            TokensRequested = 0,
            CreatedAt = request.EnqueuedAt
        };
    }

    private void TryAddItem(QueueItem item)
    {
        _allItems[item.Id] = item;
    }

    private void UpdateItemStatus(QueueItem item, QueueItemStatus status)
    {
        var updated = item with { Status = status };
        _allItems[item.Id] = updated;
    }

    private void UpdateItem(QueueItem item)
    {
        var wasTerminal = _allItems.TryGetValue(item.Id, out var prev)
            && prev.Status is QueueItemStatus.Completed or QueueItemStatus.Failed;

        _allItems[item.Id] = item;
        if (item.Status is QueueItemStatus.Completed or QueueItemStatus.Failed)
        {
            _recentCompleted.Enqueue(item);
            // Keep only last 100 completed items
            while (_recentCompleted.Count > 100)
            {
                _recentCompleted.TryDequeue(out _);
            }

            // Track terminal entries once (re-updates of an already-terminal item
            // must not double-count) so _allItems can be pruned below.
            if (!wasTerminal)
            {
                _terminalOrder.Enqueue(item.Id);
                Interlocked.Increment(ref _terminalCount);
                PruneTerminalItems();
            }
        }
    }

    /// <summary>
    /// Keeps at most <see cref="MaxTerminalTrackedItems"/> terminal rows in
    /// _allItems, evicting the oldest. Recent completions remain available via
    /// _recentCompleted; Waiting items are never touched.
    /// </summary>
    private void PruneTerminalItems()
    {
        while (Volatile.Read(ref _terminalCount) > MaxTerminalTrackedItems
               && _terminalOrder.TryDequeue(out var oldestId))
        {
            Interlocked.Decrement(ref _terminalCount);
            if (_allItems.TryGetValue(oldestId, out var old)
                && old.Status is QueueItemStatus.Completed or QueueItemStatus.Failed)
            {
                _allItems.TryRemove(oldestId, out _);
            }
        }
    }

    internal bool CancelItem(string itemId)
    {
        if (!_allItems.TryGetValue(itemId, out var item))
            return false;

        if (item.Status is QueueItemStatus.Completed or QueueItemStatus.Failed)
            return false;

        if (_requests.TryGetValue(itemId, out var request))
        {
            request.Tcs.TrySetCanceled();
        }

        FailItem(item, "Cancelled by user");
        return true;
    }

    private void FailItem(QueueItem item, string errorMessage)
    {
        var updated = item with
        {
            Status = QueueItemStatus.Failed,
            ErrorMessage = errorMessage
        };
        UpdateItem(updated);
    }

    private sealed class HostOnlyTargetResolver : IModelTargetResolver
    {
        public Task<string> ResolveTargetAsync(string modelName, CancellationToken ct = default)
            => Task.FromResult(ExecutionTarget.HostId);
    }
}