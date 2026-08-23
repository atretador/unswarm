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
/// Multi-target, lane-based non-preemptive scheduler. A dispatcher reads requests from
/// a global bounded channel, resolves the model's registered runtime and execution
/// target ("host" | "agent:&lt;name&gt;"), and routes the request into the per-runtime
/// lane for that (target, runtime) pair. A single event-driven scheduler wakes on
/// enqueues and completions, scans lanes in creation order, and starts lane heads when
/// capacity (<see cref="RegisteredRuntime.MaxConcurrentInferences"/>),
/// <see cref="CoexistencePolicy"/> coexistence, and skip-budget rules allow. Each
/// started item runs as a fire-and-forget task; container stop/start switching is
/// serialized per lane and scoped to the lane's target only.
/// </summary>
public sealed class SchedulerWorker
{
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

    // ── Lane state ────────────────────────────────────────────────────────────

    /// <summary>Per-target grouping of runtime lanes, keyed by target id.</summary>
    private readonly ConcurrentDictionary<string, TargetGroup> _targets = new(StringComparer.Ordinal);

    /// <summary>Lanes in creation order — the scheduler's stable scan order.</summary>
    private readonly List<RuntimeLane> _laneOrder = new();
    private readonly object _laneOrderLock = new();

    /// <summary>
    /// Registered runtime entities by id, populated at dispatch/route time and used
    /// for coexistence and exclusivity decisions without hitting the registry.
    /// </summary>
    private readonly ConcurrentDictionary<string, RegisteredRuntime> _runtimeCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Shared wake signal for the scheduler loop. Written on every enqueue and every
    /// inference completion; the scheduler drains it and re-scans all lanes.
    /// </summary>
    private readonly Channel<object?> _wake = Channel.CreateUnbounded<object?>(new UnboundedChannelOptions
    {
        SingleReader = true
    });

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

        // Publish total queue depth (global channel + all lane channels) to the
        // "unswarm.queue.depth" gauge. No-op unless an OTel provider listens to the
        // "Unswarm" meter.
        _queueDepthRegistration = Telemetry.UnswarmMetrics.RegisterQueueDepthProvider(GetTotalQueueDepth);

        // Expose queue depth to the stats tracker so the dashboard shows real values.
        _statsTracker.SetQueueDepthProvider(GetTotalQueueDepth);
    }

    /// <summary>Total requests waiting across the global channel and all lane channels.</summary>
    private long GetTotalQueueDepth()
    {
        var depth = (long)_channel.Reader.Count;
        foreach (var lane in SnapshotLanes())
        {
            depth += lane.Pending.Reader.Count;
        }
        return depth;
    }

    /// <summary>
    /// Returns live settings from the database when an ISettingsStore is available,
    /// otherwise falls back to the injected snapshot. Called once per scheduling step /
    /// switch (not per queued item) to avoid excessive DB reads.
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

    // ── Lane registry helpers ─────────────────────────────────────────────────

    private List<RuntimeLane> SnapshotLanes()
    {
        lock (_laneOrderLock)
        {
            return _laneOrder.ToList();
        }
    }

    private void RegisterLaneOrder(RuntimeLane lane)
    {
        lock (_laneOrderLock)
        {
            if (!_laneOrder.Contains(lane))
                _laneOrder.Add(lane);
        }
    }

    private void WakeScheduler() => _wake.Writer.TryWrite(null);

    /// <summary>
    /// Returns the target group for <paramref name="targetId"/>, creating it if needed.
    /// Owns the target-scoped running-container registry used by switch/coexistence paths.
    /// </summary>
    private TargetGroup GetTargetGroup(string targetId) =>
        _targets.GetOrAdd(targetId, _ => new TargetGroup { TargetId = targetId });

    /// <summary>
    /// Cached-entity coexistence check for the scheduler hot path. Unknown runtimes
    /// are treated as NOT coexistable (conservative); identical ids always coexist.
    /// </summary>
    private bool CanCoexist(string runtimeIdA, string runtimeIdB)
    {
        if (string.Equals(runtimeIdA, runtimeIdB, StringComparison.Ordinal))
            return true;

        return _runtimeCache.TryGetValue(runtimeIdA, out var a)
            && _runtimeCache.TryGetValue(runtimeIdB, out var b)
            && CoexistencePolicy.IsAllowedToCoexist(a, b);
    }

    /// <summary>
    /// Resolves a registered runtime entity by id, going through the cache first.
    /// Returns null when no registry is configured or the id is unknown.
    /// </summary>
    private async Task<RegisteredRuntime?> GetRuntimeEntityAsync(string runtimeId, CancellationToken ct)
    {
        if (_containerRegistry is null)
            return null;

        if (_runtimeCache.TryGetValue(runtimeId, out var cached))
            return cached;

        var runtime = await _containerRegistry.GetAsync(runtimeId, ct).ConfigureAwait(false);
        if (runtime is not null)
            _runtimeCache[runtimeId] = runtime;

        return runtime;
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

    // ── Snapshot ──────────────────────────────────────────────────────────────

    public QueueSnapshot GetSnapshot()
    {
        lock (_snapshotLock)
        {
            var lanes = SnapshotLanes();

            var processing = _allItems.Values
                .Where(i => i.Status == QueueItemStatus.Processing)
                .OrderBy(i => i.CreatedAt)
                .ToList();

            var current = processing.FirstOrDefault();

            // Distinct in-flight runtime ids — the blocking universe for waiting items.
            var inFlightRuntimes = new List<string>();
            foreach (var lane in lanes)
            {
                if (Volatile.Read(ref lane.ActiveInferences) > 0)
                    inFlightRuntimes.Add(lane.RuntimeId);
            }

            // Flattened pending view across lanes in lane-creation order; within a
            // lane, priority then age. Blocking runtime ids are computed at snapshot
            // time using the same coexistence rules the scheduler applies.
            var waiting = new List<QueueItem>();
            var seen = new HashSet<string>();
            foreach (var lane in lanes)
            {
                foreach (var item in _allItems.Values
                    .Where(i => i.Status == QueueItemStatus.Waiting
                                && i.TargetId == lane.TargetId
                                && i.RuntimeId == lane.RuntimeId)
                    .OrderBy(i => i.Priority)
                    .ThenBy(i => i.CreatedAt))
                {
                    seen.Add(item.Id);
                    waiting.Add(item with { BlockedByRuntimeIds = ComputeBlockedBy(item, inFlightRuntimes) });
                }
            }

            // Safety net: waiting items whose lane is unknown (should not happen).
            waiting.AddRange(_allItems.Values
                .Where(i => i.Status == QueueItemStatus.Waiting && !seen.Contains(i.Id))
                .OrderBy(i => i.CreatedAt)
                .Select(i => i with { BlockedByRuntimeIds = ComputeBlockedBy(i, inFlightRuntimes) }));

            var recent = _recentCompleted.ToArray()
                .OrderByDescending(i => i.CreatedAt)
                .Take(20)
                .ToList();

            var transitions = _activeTransitions.Values
                .Where(t => t.Status != "complete")
                .ToList();

            // Aggregate skip-budget state across lanes (root-level view for the dashboard).
            var skipsUsed = lanes.Sum(l => Math.Max(0, Volatile.Read(ref l.SkipsUsed)));
            var skipsRemaining = Math.Max(0, ClampSkipLimit(_settings.ParallelSlotSkipLimit) - skipsUsed);
            if (!_settings.EnableParallelSlotSkip)
                skipsRemaining = 0;

            return new QueueSnapshot
            {
                CurrentSlot = current,
                Processing = processing,
                Waiting = waiting,
                RecentCompleted = recent,
                ActiveTransitions = transitions,
                SkipsUsed = skipsUsed,
                SkipsRemaining = skipsRemaining
            };
        }
    }

    /// <summary>Clamps a skip-limit setting to its valid range [1, 1000].</summary>
    private static int ClampSkipLimit(int value) => Math.Clamp(value, 1, 1000);

    /// <summary>
    /// Computes which in-flight runtime ids block <paramref name="item"/> under the
    /// scheduler's coexistence rules (a different in-flight runtime that may not run
    /// alongside the item's runtime). Same-runtime in-flight work never blocks here —
    /// that is a capacity concern, not a coexistence one.
    /// </summary>
    private IReadOnlyList<string> ComputeBlockedBy(QueueItem item, List<string> inFlightRuntimes)
    {
        if (inFlightRuntimes.Count == 0 || item.RuntimeId is null)
            return [];

        List<string>? blocked = null;
        foreach (var runtimeId in inFlightRuntimes)
        {
            if (!CanCoexist(item.RuntimeId, runtimeId))
                (blocked ??= []).Add(runtimeId);
        }

        return (IReadOnlyList<string>?)blocked ?? Array.Empty<string>();
    }

    // ── Dispatcher ────────────────────────────────────────────────────────────

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        _logStore.Enqueue(LogLevel.Info, "Scheduler", "Scheduler worker started");

        var schedulerTask = SchedulerLoopAsync(stoppingToken);

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

        // Shutdown drain: every item still queued (global channel + every lane's
        // pending channel) must have its Tcs completed so awaiting HTTP handlers
        // return promptly instead of hanging until their client times out.
        DrainQueuedItemsOnShutdown(stoppingToken);

        // Exit only once every lane has fully drained its in-flight work.
        while (SnapshotLanes().Any(l => Volatile.Read(ref l.ActiveInferences) > 0))
        {
            try
            {
                await Task.Delay(50, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        try
        {
            await schedulerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }

        _logStore.Enqueue(LogLevel.Info, "Scheduler", "Scheduler worker stopped");
    }

    /// <summary>
    /// Completes the Tcs of every request still sitting in the global channel or in
    /// any lane's pending channel. Best-effort: the scheduler may concurrently start
    /// an item during shutdown, in which case its own cancellation path resolves it.
    /// </summary>
    private void DrainQueuedItemsOnShutdown(CancellationToken stoppingToken)
    {
        while (_channel.Reader.TryRead(out var pending))
            FailPendingOnShutdown(pending, stoppingToken);

        foreach (var lane in SnapshotLanes())
        {
            while (lane.Pending.Reader.TryRead(out var queuedItem))
            {
                FailItem(queuedItem, "Scheduler shutting down");
                if (_requests.TryGetValue(queuedItem.Id, out var request))
                    request.Tcs.TrySetCanceled(stoppingToken);
            }
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
        // ── Model → registered runtime routing (no legacy fallbacks) ──────────
        if (_containerRegistry is null)
        {
            const string message = "No container registry configured; cannot route models to runtimes";
            FailItem(queueItem, message);
            request.Tcs.TrySetException(new InvalidOperationException(message));
            return;
        }

        var runtimeId = await _containerRegistry
            .GetContainerIdForModelAsync(request.ModelName, ct)
            .ConfigureAwait(false);
        var runtime = runtimeId is not null
            ? await GetRuntimeEntityAsync(runtimeId, ct).ConfigureAwait(false)
            : null;

        if (runtimeId is null || runtime is null)
        {
            var message = $"Model '{request.ModelName}' is not mapped to a registered runtime";
            FailItem(queueItem, message);
            request.Tcs.TrySetException(new InvalidOperationException(message));
            return;
        }

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
            && !_targets.ContainsKey(targetId)
            && _targets.Count >= _settings.MaxConcurrentTargets)
        {
            FailItem(queueItem, $"Max concurrent targets ({_settings.MaxConcurrentTargets}) exceeded");
            request.Tcs.TrySetException(new InvalidOperationException(
                $"Max concurrent targets ({_settings.MaxConcurrentTargets}) exceeded for model {request.ModelName}"));
            return;
        }

        var lane = await GetOrCreateLaneAsync(targetId, runtime, ct).ConfigureAwait(false);

        var routed = queueItem with { TargetId = targetId, RuntimeId = runtime.Id };
        _allItems[request.Id] = routed;

        await lane.Pending.Writer.WriteAsync(routed, ct).ConfigureAwait(false);
        WakeScheduler();
    }

    private async Task<RuntimeLane> GetOrCreateLaneAsync(string targetId, RegisteredRuntime runtime, CancellationToken ct)
    {
        var group = _targets.GetOrAdd(targetId, _ => new TargetGroup { TargetId = targetId });

        if (group.Lanes.TryGetValue(runtime.Id, out var existing))
        {
            _runtimeCache[runtime.Id] = runtime;
            return existing;
        }

        // Live settings: queue depth changes apply to newly created lanes.
        var currentSettings = await GetCurrentSettingsAsync(ct).ConfigureAwait(false);
        var depth = ClampQueueDepth(currentSettings.MaxQueueDepth);

        var created = new RuntimeLane
        {
            TargetId = targetId,
            RuntimeId = runtime.Id,
            Pending = Channel.CreateBounded<QueueItem>(new BoundedChannelOptions(depth)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true
            }),
            MaxConcurrency = Math.Max(1, runtime.MaxConcurrentInferences)
        };

        var lane = group.Lanes.GetOrAdd(runtime.Id, created);
        _runtimeCache[runtime.Id] = runtime;
        RegisterLaneOrder(lane);
        return lane;
    }

    // ── Event-driven lane scheduler ───────────────────────────────────────────

    /// <summary>
    /// Single scheduling loop: consumes merged wake signals (enqueue + every
    /// completion), scans lanes in creation order, and starts startable lane heads
    /// until no further progress is possible, then parks on the next wake.
    /// </summary>
    private async Task SchedulerLoopAsync(CancellationToken ct)
    {
        _logStore.Enqueue(LogLevel.Info, "Scheduler", "Lane scheduler started");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Drain accumulated wakes; a fresh scan covers everything.
                while (_wake.Reader.TryRead(out _))
                {
                }

                bool progressed;
                try
                {
                    progressed = await TryStartWorkAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Scheduling step failed; retrying after delay");
                    await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                if (!progressed)
                    await _wake.Reader.WaitToReadAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
        }

        _logStore.Enqueue(LogLevel.Info, "Scheduler", "Lane scheduler stopped");
    }

    /// <summary>
    /// One scheduling pass: computes the in-flight runtime set, evaluates every
    /// non-empty lane's head via <see cref="LaneScheduler.IsStartable"/>, and starts
    /// startable heads fire-and-forget. Repeats until a full pass makes no progress.
    /// Returns true when at least one item was started.
    /// </summary>
    private async Task<bool> TryStartWorkAsync(CancellationToken ct)
    {
        // Live settings: skip toggles must take effect without a restart.
        var settings = await GetCurrentSettingsAsync(ct).ConfigureAwait(false);
        var lanes = SnapshotLanes();
        if (lanes.Count == 0)
            return false;

        var progressed = false;
        bool progressThisPass;
        do
        {
            progressThisPass = false;

            // Recomputed for EVERY start (see break below): TryStartLaneHead bumps
            // ActiveInferences synchronously, and a stale snapshot here would let
            // mutually-incompatible lanes start concurrently and deadlock each
            // other's switch drain gates.
            //
            // In-flight runtimes are grouped PER TARGET: containers on different
            // targets are independent machines — coexistence/exclusivity never
            // applies across targets.
            var inFlightByTarget = ComputeInFlightRuntimeIdsByTarget(lanes);

            // Heads that are hard-blocked right now (capacity / coexistence /
            // exclusivity — independent of the skip budget). Starting any other
            // lane's head while these exist is a bypass and consumes skip budget.
            var blockedHeads = new HashSet<RuntimeLane>();
            foreach (var lane in lanes)
            {
                if (lane.Pending.Reader.Count == 0)
                    continue;

                var others = InFlightExcluding(inFlightByTarget, lane);
                if (!IsHardStartable(lane, others))
                    blockedHeads.Add(lane);
            }

            foreach (var lane in OrderLanesForScan(lanes, settings.EnableParallelSlotSkip))
            {
                if (!lane.Pending.Reader.TryPeek(out _))
                    continue;

                var skipsRemaining = settings.EnableParallelSlotSkip
                    ? settings.ParallelSlotSkipLimit - Volatile.Read(ref lane.SkipsUsed)
                    : 0;

                var others = InFlightExcluding(inFlightByTarget, lane);
                var bypasses = blockedHeads.Count > 0 || EarlierLaneHasPending(lanes, lane);
                var laneHasCapacity = Volatile.Read(ref lane.ActiveInferences) < Math.Max(1, lane.MaxConcurrency);

                var startable = LaneScheduler.IsStartable(
                    lane,
                    others,
                    CanCoexist,
                    candidateIsExclusive: IsExclusiveRuntime(lane.RuntimeId),
                    laneHasCapacity: laneHasCapacity,
                    isHeadOfItsLane: true,
                    bypassesBlockedItem: bypasses,
                    skipEnabled: settings.EnableParallelSlotSkip,
                    skipsRemaining: skipsRemaining);

                if (!startable)
                    continue;

                if (!TryStartLaneHead(lane, bypasses, ct))
                    continue;

                progressed = true;
                progressThisPass = true;

                // Restart the scan: the start changed ActiveInferences, so the
                // in-flight set and blocked-head set must be recomputed before
                // evaluating any other lane.
                break;
            }
        }
        while (progressThisPass);

        return progressed;
    }

    /// <summary>
    /// Deterministic scan order for lane heads. With parallel slot skip DISABLED,
    /// resident-continuation lanes come first — their head runs on the still-live
    /// container without a model switch (legacy BatchDrain slot semantics: drain
    /// the resident model's queued work before churning containers). With skip
    /// ENABLED, ordering is purely by oldest head arrival time: lane-hopping is
    /// exclusively budget-mediated, so residency grants no free priority. The
    /// arrival tie-break in both modes guarantees that arbitrary lane iteration
    /// order can never reorder FIFO within a target when several runtimes are
    /// waiting on an idle machine.
    /// </summary>
    private static IEnumerable<RuntimeLane> OrderLanesForScan(
        List<RuntimeLane> lanes, bool skipEnabled)
    {
        var heads = lanes
            .Where(l => l.Pending.Reader.Count > 0)
            .Select(l => (Lane: l, Head: l.Pending.Reader.TryPeek(out var h) ? h : null))
            .Where(x => x.Head is not null)
            .ToList();

        return skipEnabled
            ? heads.OrderBy(x => x.Head!.CreatedAt).Select(x => x.Lane)
            : heads
                .OrderByDescending(x => IsResidentContinuation(x.Lane, x.Head!))
                .ThenBy(x => x.Head!.CreatedAt)
                .Select(x => x.Lane);
    }

    /// <summary>
    /// True when the lane's head can be served by its current residency without a
    /// switch. Stopping a container clears the owning lane's ResidentModel, so a
    /// non-null match implies the container is still live for this runtime.
    /// </summary>
    private static bool IsResidentContinuation(RuntimeLane lane, QueueItem head)
        => lane.ResidentModel is not null
           && string.Equals(lane.ResidentModel, head.ModelRequested, StringComparison.Ordinal);

    private static List<string> InFlightExcluding(
        Dictionary<string, List<string>> inFlightByTarget,
        RuntimeLane lane)
    {
        if (inFlightByTarget.Count == 0
            || !inFlightByTarget.TryGetValue(lane.TargetId, out var onTarget))
            return [];

        return onTarget
            .Where(r => !string.Equals(r, lane.RuntimeId, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// Distinct in-flight runtime ids per target id, computed from a fresh
    /// <see cref="RuntimeLane.ActiveInferences"/> read for every lane.
    /// </summary>
    private static Dictionary<string, List<string>> ComputeInFlightRuntimeIdsByTarget(List<RuntimeLane> lanes)
    {
        Dictionary<string, List<string>>? map = null;
        foreach (var lane in lanes)
        {
            if (Volatile.Read(ref lane.ActiveInferences) <= 0)
                continue;

            map ??= new(StringComparer.Ordinal);
            if (!map.TryGetValue(lane.TargetId, out var list))
                map[lane.TargetId] = list = [];
            list.Add(lane.RuntimeId);
        }

        return map ?? new Dictionary<string, List<string>>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Hard startability (capacity + coexistence + exclusivity) evaluated without the
    /// skip budget — used to detect blocked lane heads for bypass accounting.
    /// </summary>
    private bool IsHardStartable(RuntimeLane lane, List<string> inFlightOthers)
    {
        if (Volatile.Read(ref lane.ActiveInferences) >= Math.Max(1, lane.MaxConcurrency))
            return false;

        if (inFlightOthers.Count == 0)
            return true;

        if (IsExclusiveRuntime(lane.RuntimeId))
            return false;

        return inFlightOthers.All(other => CanCoexist(lane.RuntimeId, other));
    }

    private bool IsExclusiveRuntime(string runtimeId) =>
        !_runtimeCache.TryGetValue(runtimeId, out var runtime)
        || runtime.CanRunAlongWith.Count == 0;

    private static bool EarlierLaneHasPending(List<RuntimeLane> lanes, RuntimeLane lane)
    {
        foreach (var earlier in lanes)
        {
            if (ReferenceEquals(earlier, lane))
                return false;
            if (earlier.Pending.Reader.Count > 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Starts the head of <paramref name="lane"/>: increments <see cref="RuntimeLane.ActiveInferences"/>
    /// synchronously (so capacity gates never observe a stale zero), dequeues the head,
    /// optionally consumes skip budget, and launches the runner fire-and-forget.
    /// </summary>
    private bool TryStartLaneHead(RuntimeLane lane, bool consumeSkip, CancellationToken ct)
    {
        Interlocked.Increment(ref lane.ActiveInferences);

        if (!lane.Pending.Reader.TryRead(out var item))
        {
            // Head vanished (drained during shutdown) — undo the reservation.
            Interlocked.Decrement(ref lane.ActiveInferences);
            return false;
        }

        if (consumeSkip)
            Interlocked.Increment(ref lane.SkipsUsed);

        if (!_requests.TryGetValue(item.Id, out var request))
        {
            FailItem(item, $"Tracking lost for request {item.Id}");
            Interlocked.Decrement(ref lane.ActiveInferences);
            WakeScheduler();
            return true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunLaneItemAsync(lane, request, item, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // RunLaneItemAsync handles its own failures; this is a last-resort guard.
                _logger.LogError(ex, "Lane runner crashed for request {Id} on target {Target}",
                    item.Id, lane.TargetId);
            }
        }, CancellationToken.None);

        return true;
    }

    // ── Lane runner ───────────────────────────────────────────────────────────

    private async Task RunLaneItemAsync(RuntimeLane lane, InferenceRequest request, QueueItem queueItem, CancellationToken ct)
    {
        try
        {
            // Already resolved elsewhere (cancelled by a batch drain or shutdown):
            // never run inference for it again — the caller is long gone.
            if (request.Tcs.Task.IsCompleted)
                return;

            await ProcessRequestAsync(lane, request, queueItem, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            FailItem(queueItem, "Scheduler shutting down");
            request.Tcs.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing request {Id} on target {Target}",
                request.Id, lane.TargetId);
            FailItem(queueItem, ex.Message);
            request.Tcs.TrySetException(ex);
        }
        finally
        {
            Interlocked.Decrement(ref lane.ActiveInferences);

            // Sequential-step accounting: after QueueStepsTillReset completions on
            // this lane, reset the skip budget so future bypasses are permitted.
            try
            {
                var settings = await GetCurrentSettingsAsync(CancellationToken.None).ConfigureAwait(false);
                var steps = Interlocked.Increment(ref lane.SequentialStepsProcessed);
                if (steps >= settings.QueueStepsTillReset)
                {
                    Interlocked.Exchange(ref lane.SkipsUsed, 0);
                    Interlocked.Exchange(ref lane.SequentialStepsProcessed, 0);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update skip accounting for lane {Runtime} on {Target}",
                    lane.RuntimeId, lane.TargetId);
            }

            // Completion is a scheduling event: something may have become startable.
            WakeScheduler();
        }
    }

    private async Task ProcessRequestAsync(RuntimeLane lane, InferenceRequest request, QueueItem queueItem, CancellationToken ct)
    {
        // Track this request as active for dashboard stats
        _statsTracker.TrackActiveRequest(request.Id);
        try
        {
            await ProcessRequestInnerAsync(lane, request, queueItem, ct).ConfigureAwait(false);
        }
        finally
        {
            _statsTracker.UntrackActiveRequest(request.Id);
        }
    }

    private async Task ProcessRequestInnerAsync(RuntimeLane lane, InferenceRequest request, QueueItem queueItem, CancellationToken ct)
    {
        // Ensure the lane's runtime serves the requested model
        if (lane.ResidentModel != request.ModelName)
        {
            var runtime = await GetRuntimeEntityAsync(lane.RuntimeId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Registered runtime {lane.RuntimeId} for model {request.ModelName} not found");

            await SwitchModelAsync(lane, request.ModelName, runtime, ct).ConfigureAwait(false);
        }

        // If switch failed, the model container won't be running
        if (lane.ResidentModel != request.ModelName)
        {
            FailItem(queueItem, $"Failed to start container for model {request.ModelName}");
            request.Tcs.TrySetException(new InvalidOperationException($"Container for model {request.ModelName} not available"));
            return;
        }

        // Process the request
        UpdateItemStatus(queueItem, QueueItemStatus.Processing);
        _logStore.Enqueue(LogLevel.Info, "Scheduler",
            $"Processing request {request.Id} for model {request.ModelName} on {lane.TargetId} (runtime {lane.RuntimeId})");

        // Declared outside the try so the catch filters can distinguish a request
        // timeout from scheduler shutdown.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.RequestTimeout));

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token, request.CancellationToken);

            var response = await _inference.InvokeAsync(request, linkedCts.Token).ConfigureAwait(false);

            // RequestTimeout only covers time-to-first-response; never abort an active stream drain.
            timeoutCts.CancelAfter(Timeout.InfiniteTimeSpan);

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
            // releasing the lane slot. This prevents the next request from
            // triggering a model switch that would kill the upstream container
            // still serving the active stream.
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
                        $"Stream drain cancelled for request {request.Id} on {lane.TargetId}");
                }
                catch (Exception ex)
                {
                    // Drain fault (upstream disconnect, etc.) — log and continue.
                    // The request is already completed; this must not propagate.
                    _logStore.Enqueue(LogLevel.Warn, "Scheduler",
                        $"Stream drain faulted for request {request.Id} on {lane.TargetId}: {ex.Message}");
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
            // Shutdown (the runner may observe cancellation mid-flight): cancel the
            // caller, never fake a timeout.
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

    // ── Lane-scoped model switching ───────────────────────────────────────────

    private async Task SwitchModelAsync(RuntimeLane lane, string targetModel, RegisteredRuntime targetRuntime, CancellationToken ct)
    {
        // Serialize switches per lane: concurrent (coexistence-allowed) requests may
        // each trigger a switch; container state mutations must not interleave.
        await lane.SwitchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SwitchModelLockedAsync(lane, targetModel, targetRuntime, ct).ConfigureAwait(false);
        }
        finally
        {
            lane.SwitchLock.Release();
        }
    }

    private async Task SwitchModelLockedAsync(RuntimeLane lane, string targetModel, RegisteredRuntime targetRuntime, CancellationToken ct)
    {
        // Drain gate: only wait for active inferences to finish when the switch will
        // STOP one of their containers (incompatible runtime). Coexistence-compatible
        // runtimes start alongside without draining. Same-lane in-flight work always
        // shares this lane's runtime container, so only sibling lanes matter here.
        if (!await CanRunAlongsideRunningAsync(lane, targetRuntime, ct).ConfigureAwait(false))
        {
            while (AnyIncompatibleInFlightOnTarget(lane, targetRuntime))
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }

        var transitionId = Guid.NewGuid().ToString("N");
        var fromModel = lane.ResidentModel ?? "(none)";
        var switchStart = _clock.UtcNow;

        Telemetry.UnswarmMetrics.RecordModelSwitch(fromModel, targetModel, lane.TargetId);

        var transition = new ModelTransition
        {
            Id = transitionId,
            FromModel = fromModel,
            ToModel = targetModel,
            RuntimeId = lane.RuntimeId,
            Status = "switching",
            StartedAt = _clock.UtcNow
        };
        _activeTransitions[transitionId] = transition;

        _logStore.Enqueue(LogLevel.Info, "Scheduler",
            $"Switching model on {lane.TargetId} (runtime {lane.RuntimeId}): {fromModel} -> {targetModel}");

        try
        {
            // Load live settings once per switch for LazyStop / BatchDrain evaluation
            var currentSettings = await GetCurrentSettingsAsync(ct).ConfigureAwait(false);

            // Update concurrency limit from the registered runtime.
            // Only safe when nothing is in flight — with coexistence-allowed
            // switches, active inferences may still be running; the next quiet
            // switch picks up the new limit.
            if (Volatile.Read(ref lane.ActiveInferences) == 0)
            {
                var newMax = Math.Max(1, targetRuntime.MaxConcurrentInferences);
                lane.MaxConcurrency = newMax;
                var oldGate = lane.ConcurrencyGate;
                lane.ConcurrencyGate = new SemaphoreSlim(newMax, newMax);
                oldGate?.Dispose();
            }

            // This lane's runtime container is already up → instant switch (covers
            // multi-model runtimes: the model changed but the container did not).
            var targetGroup = GetTargetGroup(lane.TargetId);
            if (targetGroup.RunningContainers.TryGetValue(lane.RuntimeId, out var alreadyRunning))
            {
                _logStore.Enqueue(LogLevel.Info, "Scheduler",
                    $"Instant switch on {lane.TargetId}: {fromModel} -> {targetModel} (container {alreadyRunning.ContainerName} already running)");

                lane.ResidentModel = targetModel;
                lane.ResidentContainerId = alreadyRunning.ContainerId;

                var switchDurationMs = (_clock.UtcNow - switchStart).TotalMilliseconds;
                _statsTracker.RecordSwitch(switchDurationMs);

                transition = transition with { Status = "complete" };
                _activeTransitions[transitionId] = transition;

                _logStore.Enqueue(LogLevel.Info, "Scheduler",
                    $"Instant model switch complete on {lane.TargetId}: now running {targetModel} ({switchDurationMs:F0}ms)");
                return;
            }

            // Drain: if LazyStop, batch-drain all requests for the current model first.
            // When BatchDrain=false, skip the drain and proceed with minimal stop/switch.
            if (currentSettings.LazyStop && currentSettings.BatchDrain
                && lane.ResidentContainerId is not null && lane.ResidentModel is not null)
            {
                transition = transition with { Status = "draining" };
                _activeTransitions[transitionId] = transition;

                await DrainCurrentModelAsync(lane, ct).ConfigureAwait(false);
            }

            // Stop incompatible running containers on this target (canRunAlongWith)
            transition = transition with { Status = "switching" };
            _activeTransitions[transitionId] = transition;

            await StopIncompatibleContainersAsync(lane, targetRuntime, ct).ConfigureAwait(false);

            // Start new container
            transition = transition with { Status = "starting" };
            _activeTransitions[transitionId] = transition;

            // Script runtime: start via the appropriate controller instead of Docker
            if (targetRuntime.RuntimeKind == RuntimeKind.Script)
            {
                var launcherPath = targetRuntime.LauncherPath
                    ?? throw new InvalidOperationException($"Script runtime {targetRuntime.Id} has no LauncherPath");

                int scriptPid;
                try
                {
                    var isHost = string.Equals(lane.TargetId, ExecutionTarget.HostId, StringComparison.OrdinalIgnoreCase);
                    if (!isHost)
                    {
                        // Agent-hosted script: start via RemoteAgentDockerController
                        var scriptController = _router.GetController(lane.TargetId);
                        if (scriptController is not RemoteAgentDockerController remoteScriptController)
                        {
                            throw new InvalidOperationException($"Agent target '{lane.TargetId}' does not have a connected RemoteAgentDockerController");
                        }
                        scriptPid = await remoteScriptController.StartScriptAsync(launcherPath, targetRuntime.ContainerPort, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // Host script: start via HostScriptRuntimeController
                        if (_scriptController is null)
                            throw new InvalidOperationException("HostScriptRuntimeController not available");

                        var scriptResult = await _scriptController.StartScriptAsync(
                            targetRuntime.Id, launcherPath, targetRuntime.ContainerPort, ct).ConfigureAwait(false);

                        if (scriptResult.ErrorMessage is not null)
                        {
                            _logStore.Enqueue(LogLevel.Error, "Scheduler",
                                $"Script start failed on {lane.TargetId} for model {targetModel}: {scriptResult.ErrorMessage}");

                            FailAllForModel(targetModel, lane.TargetId, $"Script start failed: {scriptResult.ErrorMessage}");

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
                        $"Script start failed on {lane.TargetId} for model {targetModel}: {ex.Message}");

                    FailAllForModel(targetModel, lane.TargetId, $"Script start failed: {ex.Message}");

                    transition = transition with { Status = "complete" };
                    _activeTransitions[transitionId] = transition;
                    return;
                }

                var scriptKey = $"script:{targetRuntime.Id}";
                var scriptPort = targetRuntime.ContainerPort;

                // Wait for health
                var healthHost = ResolveHealthCheckHost(lane.TargetId);
                await _healthChecker.WaitForReadyAsync(scriptPort, healthHost, _settings.HealthCheckTimeoutSeconds, ct).ConfigureAwait(false);

                lane.ResidentModel = targetModel;
                lane.ResidentContainerId = scriptKey;

                GetTargetGroup(lane.TargetId).RunningContainers[lane.RuntimeId] = new RunningContainerInfo
                {
                    Key = lane.RuntimeId,
                    RegisteredRuntimeId = lane.RuntimeId,
                    ContainerName = targetRuntime.DisplayName ?? targetRuntime.Image,
                    ContainerId = scriptKey
                };

                var switchDurationScript = (_clock.UtcNow - switchStart).TotalMilliseconds;
                _statsTracker.RecordSwitch(switchDurationScript);

                transition = transition with { Status = "complete" };
                _activeTransitions[transitionId] = transition;

                _logStore.Enqueue(LogLevel.Info, "Scheduler",
                    $"Script switch complete on {lane.TargetId}: now running {targetModel} on port {scriptPort} ({switchDurationScript:F0}ms)");
                _ = scriptPid;
                return;
            }

            var controller = _router.GetController(lane.TargetId);
            var maxRetries = _settings.MaxContainerStartRetries;
            ContainerStartResult? startResult = null;
            Exception? lastException = null;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    startResult = await controller.StartRegisteredContainerAsync(
                        targetRuntime.Id,
                        targetRuntime.Image,
                        targetRuntime.ContainerPort,
                        targetRuntime.GpuDevices,
                        targetRuntime.MemoryLimitMb,
                        targetRuntime.ExtraLabels ?? new Dictionary<string, string>(),
                        ct).ConfigureAwait(false);

                    if (startResult.ErrorMessage is null)
                        break; // Success

                    lastException = new InvalidOperationException(startResult.ErrorMessage);
                    _logStore.Enqueue(LogLevel.Warn, "Scheduler",
                        $"Container start attempt {attempt}/{maxRetries} failed on {lane.TargetId} for model {targetModel}: {startResult.ErrorMessage}");

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
                        $"Container start attempt {attempt}/{maxRetries} threw on {lane.TargetId} for model {targetModel}: {ex.Message}");

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
                    $"Container start failed after {maxRetries} attempts on {lane.TargetId} for model {targetModel}: {errorMsg}");

                FailAllForModel(targetModel, lane.TargetId, $"Container start failed after {maxRetries} attempts: {errorMsg}");

                transition = transition with { Status = "complete" };
                _activeTransitions[transitionId] = transition;
                return;
            }

            // Wait for health
            if (startResult.MappedPort.HasValue)
            {
                var healthHost = ResolveHealthCheckHost(lane.TargetId);
                await _healthChecker.WaitForReadyAsync(startResult.MappedPort.Value, healthHost, _settings.HealthCheckTimeoutSeconds, ct).ConfigureAwait(false);
            }

            lane.ResidentModel = targetModel;
            lane.ResidentContainerId = startResult.ContainerId;

            // Persist the live container id so the registry converges when the docker
            // container was recreated (same name, new id) outside this scheduler —
            // otherwise stop/status paths keep operating on the stale id.
            if (!string.IsNullOrEmpty(startResult.ContainerId)
                && !string.Equals(startResult.ContainerId, targetRuntime.RuntimeContainerId, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await _containerRegistry!.UpdateAsync(targetRuntime.Id, targetRuntime with
                    {
                        RuntimeContainerId = startResult.ContainerId
                    }, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to refresh RuntimeContainerId for registered runtime {RegId} after scheduler start",
                        targetRuntime.Id);
                }
            }

            GetTargetGroup(lane.TargetId).RunningContainers[lane.RuntimeId] = new RunningContainerInfo
            {
                Key = lane.RuntimeId,
                RegisteredRuntimeId = lane.RuntimeId,
                ContainerName = targetRuntime.Image,
                ContainerId = startResult.ContainerId
            };

            var switchDurationMs3 = (_clock.UtcNow - switchStart).TotalMilliseconds;
            _statsTracker.RecordSwitch(switchDurationMs3);

            transition = transition with { Status = "complete" };
            _activeTransitions[transitionId] = transition;

            _logStore.Enqueue(LogLevel.Info, "Scheduler",
                $"Model switch complete on {lane.TargetId}: now running {targetModel} on port {startResult.MappedPort} ({switchDurationMs3:F0}ms)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model switch failed on {Target}: {From} -> {To}", lane.TargetId, fromModel, targetModel);

            // Fail all queued requests for the target model
            FailAllForModel(targetModel, lane.TargetId, $"Model switch failed: {ex.Message}");

            transition = transition with { Status = "complete" };
            _activeTransitions[transitionId] = transition;

            throw;
        }
    }

    /// <summary>
    /// Stops containers tracked on this lane's target that cannot run alongside the
    /// target runtime. Compatibility is decided by the shared symmetric
    /// <see cref="CoexistencePolicy"/> (each side must allow-list the other; empty
    /// list = runs alone). Every tracked entry carries its registered runtime id —
    /// there are no legacy keys, so all entries are safe stop candidates.
    /// </summary>
    private async Task StopIncompatibleContainersAsync(RuntimeLane lane, RegisteredRuntime targetRuntime, CancellationToken ct)
    {
        // Live-state merge first: containers started outside the scheduler
        // (registration auto-start, manual start, backend restart survivors)
        // must be visible here or they will never be stopped.
        await ReconcileRunningContainersAsync(lane, ct).ConfigureAwait(false);

        var runningOnTarget = GetTargetGroup(lane.TargetId).RunningContainers;

        var toStop = new List<string>();
        foreach (var kv in runningOnTarget)
        {
            var info = kv.Value;

            // Same registered runtime → never stop
            if (string.Equals(info.RegisteredRuntimeId, targetRuntime.Id, StringComparison.Ordinal))
                continue;

            var runningEntity = await _containerRegistry!.GetAsync(info.RegisteredRuntimeId, ct).ConfigureAwait(false);
            if (runningEntity is null)
            {
                toStop.Add(kv.Key);
                continue;
            }

            // Symmetric compatibility via the shared policy: each side must allow the other
            if (!CoexistencePolicy.IsAllowedToCoexist(targetRuntime, runningEntity))
                toStop.Add(kv.Key);
        }

        var controller = _router.GetController(lane.TargetId);
        foreach (var key in toStop)
        {
            var info = runningOnTarget[key];

            _logStore.Enqueue(LogLevel.Info, "Scheduler",
                $"Stopping incompatible container {info.ContainerName} on {lane.TargetId}");

            // Script runtimes: dispatch stop to the correct controller
            if (info.ContainerId.StartsWith("script:", StringComparison.Ordinal))
            {
                await StopScriptRuntimeAsync(lane.TargetId, info.RegisteredRuntimeId, ct).ConfigureAwait(false);
            }
            else
            {
                var stopTargetId = await ResolveStopTargetIdAsync(controller, info, ct).ConfigureAwait(false);
                await controller.StopContainerAsync(stopTargetId, ct).ConfigureAwait(false);
            }

            runningOnTarget.TryRemove(key, out _);

            // The stopped container may have been the resident of ANY lane on this
            // target (sibling lanes included) — clear stale residency so the next
            // request on that lane re-runs the switch instead of hitting a dead container.
            if (GetTargetGroup(lane.TargetId).Lanes.TryGetValue(key, out var ownerLane)
                && string.Equals(ownerLane.ResidentContainerId, info.ContainerId, StringComparison.Ordinal))
            {
                ownerLane.ResidentModel = null;
                ownerLane.ResidentContainerId = null;
            }

            if (lane.ResidentContainerId == info.ContainerId)
            {
                lane.ResidentModel = null;
                lane.ResidentContainerId = null;
            }
        }
    }

    /// <summary>
    /// Merges live container state on this lane's target into
    /// <see cref="RuntimeLane.RunningContainers"/>. Only containers that resolve to a
    /// registered runtime (docker-label path first, then registry RuntimeContainerId
    /// match) are tracked — there are no legacy keys. ADDITIVE ONLY: never removes
    /// entries (a transient empty listing must not make the scheduler forget
    /// containers it started).
    /// </summary>
    private async Task ReconcileRunningContainersAsync(RuntimeLane lane, CancellationToken ct)
    {
        try
        {
            var controller = _router.GetController(lane.TargetId);
            var containers = await controller.ListContainersAsync(ct).ConfigureAwait(false);

            IReadOnlyList<RegisteredRuntime> allRuntimes =
                _containerRegistry is not null
                    ? await _containerRegistry.ListAllAsync(ct).ConfigureAwait(false)
                    : Array.Empty<RegisteredRuntime>();

            var runningOnTarget = GetTargetGroup(lane.TargetId).RunningContainers;

            foreach (var c in containers.Where(c => c.Status == ContainerStatus.Running))
            {
                // Resolve the registered runtime: docker-label path first,
                // then registry RuntimeContainerId match. Unresolvable containers
                // are not tracked (no legacy keys).
                var regId = c.RegisteredRuntimeId;
                if (regId is null)
                    regId = allRuntimes.FirstOrDefault(r =>
                        r.RuntimeContainerId is not null &&
                        string.Equals(r.RuntimeContainerId, c.Id, StringComparison.OrdinalIgnoreCase))?.Id;

                if (regId is null || runningOnTarget.ContainsKey(regId))
                    continue;

                runningOnTarget[regId] = new RunningContainerInfo
                {
                    Key = regId,
                    RegisteredRuntimeId = regId,
                    ContainerName = c.ModelName,
                    ContainerId = c.Id
                };
                _logStore.Enqueue(LogLevel.Info, "Scheduler",
                    $"Reconciled externally-started container {c.ModelName} ({c.Id[..Math.Min(12, c.Id.Length)]}) into target {lane.TargetId}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reconcile running containers on {Target}", lane.TargetId);
        }
    }

    /// <summary>
    /// Coexistence-aware eligibility for a model switch on this lane: returns true
    /// when the candidate runtime may run alongside everything currently active on
    /// the target — sibling lanes with in-flight work AND every tracked running
    /// container (symmetric policy check, mirroring
    /// <see cref="StopIncompatibleContainersAsync"/>). When true, the switch keeps
    /// compatible containers alive instead of draining.
    /// </summary>
    private async Task<bool> CanRunAlongsideRunningAsync(RuntimeLane lane, RegisteredRuntime candidate, CancellationToken ct)
    {
        if (_containerRegistry is null)
            return false;

        // Live-state merge first: externally-started / restart-surviving
        // containers must participate in the coexistence decision.
        await ReconcileRunningContainersAsync(lane, ct).ConfigureAwait(false);

        // Sibling lanes with in-flight work must be compatible. Same-lane in-flight
        // work shares the candidate's container and is always compatible.
        if (_targets.TryGetValue(lane.TargetId, out var group))
        {
            foreach (var sibling in group.Lanes.Values)
            {
                if (ReferenceEquals(sibling, lane))
                    continue;
                if (Volatile.Read(ref sibling.ActiveInferences) == 0)
                    continue;
                if (!CanCoexist(candidate.Id, sibling.RuntimeId))
                    return false;
            }
        }

        foreach (var kv in GetTargetGroup(lane.TargetId).RunningContainers)
        {
            var info = kv.Value;

            // Same registered runtime → compatible by definition.
            if (string.Equals(info.RegisteredRuntimeId, candidate.Id, StringComparison.Ordinal))
                continue;

            var runningEntity = await _containerRegistry.GetAsync(info.RegisteredRuntimeId, ct).ConfigureAwait(false);
            if (runningEntity is null || !CoexistencePolicy.IsAllowedToCoexist(candidate, runningEntity))
                return false;
        }

        return true;
    }

    /// <summary>
    /// True when any sibling lane on this lane's target currently has in-flight work
    /// on a runtime that cannot coexist with <paramref name="candidate"/> — i.e. the
    /// switch would stop a container actively serving a request.
    /// </summary>
    private bool AnyIncompatibleInFlightOnTarget(RuntimeLane lane, RegisteredRuntime candidate)
    {
        if (!_targets.TryGetValue(lane.TargetId, out var group))
            return false;

        foreach (var sibling in group.Lanes.Values)
        {
            if (ReferenceEquals(sibling, lane))
                continue;
            if (Volatile.Read(ref sibling.ActiveInferences) == 0)
                continue;
            if (!CanCoexist(candidate.Id, sibling.RuntimeId))
                return true;
        }

        return false;
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
    private Task DrainCurrentModelAsync(RuntimeLane lane, CancellationToken ct)
    {
        if (lane.ResidentModel is null) return Task.CompletedTask;

        // Collect all waiting requests for the current model on this target
        var toDrain = _allItems.Values
            .Where(i => i.Status == QueueItemStatus.Waiting && i.ModelRequested == lane.ResidentModel && i.TargetId == lane.TargetId)
            .OrderBy(i => i.Priority)
            .ThenBy(i => i.CreatedAt)
            .ToList();

        if (toDrain.Count == 0) return Task.CompletedTask;

        _logStore.Enqueue(LogLevel.Info, "Scheduler",
            $"Batch-draining {toDrain.Count} requests for model {lane.ResidentModel} on {lane.TargetId}");

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
        // Fails across ALL lanes of this target serving the model — the filter is
        // model+target scoped, so coexisting lanes are covered too.
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
