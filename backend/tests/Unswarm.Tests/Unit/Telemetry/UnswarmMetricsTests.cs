using System.Diagnostics.Metrics;
using Unswarm.Core.Telemetry;

namespace Unswarm.Tests.Unit;

/// <summary>
/// Verifies the custom "Unswarm" instruments record values, observed through a
/// plain <see cref="MeterListener"/> (no exporter dependency required).
/// </summary>
public sealed class UnswarmMetricsTests : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<Measurement> _measurements = new();

    public UnswarmMetricsTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == UnswarmMetrics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>(OnLongMeasurement);
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    private sealed record Measurement(string Instrument, long Value, Dictionary<string, object?> Tags);

    private void OnLongMeasurement(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var t in tags)
        {
            dict[t.Key] = t.Value;
        }

        lock (_measurements)
        {
            _measurements.Add(new Measurement(instrument.Name, measurement, dict));
        }
    }

    private List<Measurement> Snapshot()
    {
        lock (_measurements)
        {
            return new List<Measurement>(_measurements);
        }
    }

    [Fact]
    public void RecordInferenceRequest_HostSuccess_RecordsOneWithTagSet()
    {
        UnswarmMetrics.RecordInferenceRequest("llama3", isHostTarget: true, success: true);

        var m = Snapshot().Single(x => x.Instrument == "unswarm.inference.requests" && x.Tags.GetValueOrDefault("model") as string == "llama3");
        Assert.Equal(1, m.Value);
        Assert.Equal("host", m.Tags["target.kind"]);
        Assert.Equal("ok", m.Tags["outcome"]);
    }

    [Fact]
    public void RecordInferenceRequest_AgentError_RecordsAgentAndErrorTags()
    {
        UnswarmMetrics.RecordInferenceRequest("qwen", isHostTarget: false, success: false);

        var m = Snapshot().Single(x => x.Instrument == "unswarm.inference.requests" && x.Tags.GetValueOrDefault("model") as string == "qwen");
        Assert.Equal(1, m.Value);
        Assert.Equal("agent", m.Tags["target.kind"]);
        Assert.Equal("error", m.Tags["outcome"]);
    }

    [Fact]
    public void RecordInferenceFailure_RecordsCounter()
    {
        UnswarmMetrics.RecordInferenceFailure("mistral", isHostTarget: true);

        var m = Snapshot().Single(x => x.Instrument == "unswarm.inference.failures");
        Assert.Equal(1, m.Value);
        Assert.Equal("mistral", m.Tags["model"]);
        Assert.Equal("host", m.Tags["target.kind"]);
    }

    [Fact]
    public void RecordModelSwitch_RecordsFromToTargetTags()
    {
        UnswarmMetrics.RecordModelSwitch("llama3", "qwen", "host");

        var m = Snapshot().Single(x => x.Instrument == "unswarm.model.switches");
        Assert.Equal(1, m.Value);
        Assert.Equal("llama3", m.Tags["from"]);
        Assert.Equal("qwen", m.Tags["to"]);
        Assert.Equal("host", m.Tags["target"]);
    }

    [Fact]
    public void QueueDepth_Gauge_ObservesRegisteredProviderValue()
    {
        using var registration = UnswarmMetrics.RegisterQueueDepthProvider(() => 42);

        // Force a collection cycle so the observable gauge callback runs.
        _listener.RecordObservableInstruments();

        var depths = Snapshot().Where(x => x.Instrument == "unswarm.queue.depth").ToList();
        Assert.Contains(depths, m => m.Value == 42);
    }

    [Fact]
    public void QueueDepth_WithoutProvider_EmitsNoPositiveMeasurement()
    {
        // No provider registered — gauge must emit nothing rather than zero.
        _listener.RecordObservableInstruments();

        var depths = Snapshot().Where(x => x.Instrument == "unswarm.queue.depth").ToList();
        Assert.DoesNotContain(depths, m => m.Value > 0);
    }
}
