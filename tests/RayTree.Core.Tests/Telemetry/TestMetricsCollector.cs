using System.Diagnostics.Metrics;
using RayTree.Core.Telemetry;

namespace RayTree.Core.Tests.Telemetry;

/// <summary>
/// Wraps a <see cref="MeterListener"/> scoped to a single <see cref="RayTreeMeter"/> so
/// parallel tests do not see each other's measurements. Construct one per test inside a
/// <c>using</c>: <c>using var c = new TestMetricsCollector(meter);</c>.
/// </summary>
internal sealed class TestMetricsCollector : IDisposable
{
    private readonly RayTreeMeter _meter;
    private readonly MeterListener _listener;
    private readonly List<RecordedMeasurement> _measurements = new();
    private readonly object _gate = new();

    public TestMetricsCollector(RayTreeMeter meter)
    {
        _meter = meter;
        var ownerMeter = _meter.InternalMeter;
        _listener = new MeterListener
        {
            // Filter at subscription time to this specific meter instance — parallel tests
            // each get their own RayTreeMeter and their listener ignores the others.
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, ownerMeter))
                    listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>  (RecordLong);
        _listener.SetMeasurementEventCallback<int>   (RecordInt);
        _listener.SetMeasurementEventCallback<double>(RecordDouble);

        _listener.Start();
    }

    private void RecordLong(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        => Append(instrument, (double)value, tags);

    private void RecordInt(Instrument instrument, int value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        => Append(instrument, value, tags);

    private void RecordDouble(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        => Append(instrument, value, tags);

    private void Append(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var tagDict = new Dictionary<string, object?>(tags.Length);
        foreach (var kv in tags) tagDict[kv.Key] = kv.Value;
        lock (_gate) _measurements.Add(new RecordedMeasurement(instrument.Name, value, tagDict, instrument.Unit));
    }

    /// <summary>Forces the observable gauge callback to fire (does not normally happen otherwise in tests).</summary>
    public void RecordObservableInstruments() => _listener.RecordObservableInstruments();

    /// <summary>All measurements recorded against the named instrument (most recent first).</summary>
    public IReadOnlyList<RecordedMeasurement> Get(string instrumentName)
    {
        lock (_gate)
            return _measurements
                .Where(m => m.Name == instrumentName)
                .Reverse()
                .ToList();
    }

    /// <summary>Sum of all values recorded against the named instrument (matches counter total).</summary>
    public double Sum(string instrumentName)
    {
        lock (_gate)
            return _measurements.Where(m => m.Name == instrumentName).Sum(m => m.Value);
    }

    public void Dispose() => _listener.Dispose();

    internal sealed record RecordedMeasurement(string Name, double Value, IReadOnlyDictionary<string, object?> Tags, string? Unit);
}
