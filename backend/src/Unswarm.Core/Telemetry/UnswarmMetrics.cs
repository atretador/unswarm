using System.Diagnostics.Metrics;

namespace Unswarm.Core.Telemetry;

/// <summary>
/// Custom application metrics for the Unswarm inference pipeline, published on the
/// "Unswarm" meter. Instruments are wired into the OpenTelemetry SDK by Program.cs
/// (<c>AddOpenTelemetry().AddMeter(MeterName)</c>); when no provider listens to the
/// meter (or no exporter is configured), every call is a cheap no-op — the SDK
/// instruments short-circuit without a listener.
/// </summary>
public static class UnswarmMetrics
{
    /// <summary>The meter name; must be passed to <c>AddMeter</c> to enable collection.</summary>
    public const string MeterName = "Unswarm";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>Counter of inference requests. Tags: model, target.kind, outcome.</summary>
    private static readonly Counter<long> InferenceRequests = Meter.CreateCounter<long>(
        "unswarm.inference.requests",
        unit: "{request}",
        description: "Inference requests proxied by the backend, by model, target kind and outcome.");

    /// <summary>Counter of failed inference requests. Tags: model, target.kind.</summary>
    private static readonly Counter<long> InferenceFailures = Meter.CreateCounter<long>(
        "unswarm.inference.failures",
        unit: "{request}",
        description: "Inference requests that ended in an error (HTTP >= 400, transport failure or exception).");

    /// <summary>Counter of model switches performed by the scheduler. Tags: from, to, target.</summary>
    private static readonly Counter<long> ModelSwitches = Meter.CreateCounter<long>(
        "unswarm.model.switches",
        unit: "{switch}",
        description: "Model switches executed by the scheduler.");

    /// <summary>Gauge of total queued inference requests (global channel + per-target channels).</summary>
    private static readonly ObservableGauge<long> QueueDepth = Meter.CreateObservableGauge(
        "unswarm.queue.depth",
        observeValue: ObserveQueueDepth,
        unit: "{request}",
        description: "Number of inference requests currently waiting in scheduler queues.");

    // Queue-depth observer registered by SchedulerWorker; null until then.
    private static volatile Func<long>? _queueDepthProvider;

    private static Measurement<long> ObserveQueueDepth()
    {
        var provider = _queueDepthProvider;
        if (provider is null)
            return default; // no measurement emitted

        long depth;
        try
        {
            depth = provider();
        }
        catch
        {
            return default; // never let telemetry break the pipeline
        }

        return depth >= 0 ? new Measurement<long>(depth) : default;
    }

    /// <summary>
    /// Records the outcome of one proxied inference request.
    /// </summary>
    /// <param name="model">Requested model name.</param>
    /// <param name="isHostTarget">True when the target is the host, false for a remote agent.</param>
    /// <param name="success">True when the request completed successfully.</param>
    public static void RecordInferenceRequest(string model, bool isHostTarget, bool success)
    {
        InferenceRequests.Add(1,
            new KeyValuePair<string, object?>("model", model),
            new KeyValuePair<string, object?>("target.kind", isHostTarget ? TargetKindHost : TargetKindAgent),
            new KeyValuePair<string, object?>("outcome", success ? OutcomeOk : OutcomeError));
    }

    /// <summary>
    /// Records a single failed inference request.
    /// </summary>
    public static void RecordInferenceFailure(string model, bool isHostTarget)
    {
        InferenceFailures.Add(1,
            new KeyValuePair<string, object?>("model", model),
            new KeyValuePair<string, object?>("target.kind", isHostTarget ? TargetKindHost : TargetKindAgent));
    }

    /// <summary>
    /// Records a model switch performed by the scheduler on a target.
    /// </summary>
    public static void RecordModelSwitch(string fromModel, string toModel, string targetId)
    {
        ModelSwitches.Add(1,
            new KeyValuePair<string, object?>("from", fromModel),
            new KeyValuePair<string, object?>("to", toModel),
            new KeyValuePair<string, object?>("target", targetId));
    }

    /// <summary>
    /// Registers the callback that reports total queue depth for the gauge.
    /// Returns a disposable that unregisters it (used by tests).
    /// </summary>
    public static IDisposable RegisterQueueDepthProvider(Func<long> provider)
    {
        _queueDepthProvider = provider;
        return new QueueDepthRegistration();
    }

    private sealed class QueueDepthRegistration : IDisposable
    {
        public void Dispose() => _queueDepthProvider = null;
    }

    private const string TargetKindHost = "host";
    private const string TargetKindAgent = "agent";
    private const string OutcomeOk = "ok";
    private const string OutcomeError = "error";
}
