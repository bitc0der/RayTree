using System.Diagnostics.Metrics;
using RayTree.Core.Telemetry;
using RayTree.Core.Tests.Telemetry;

namespace RayTree.Core.Tests.Resilience;

[TestFixture]
public class RecoveryMetricsTests
{
    private sealed class InstrumentCollector : IDisposable
    {
        private readonly MeterListener _listener;
        public List<Instrument> Instruments { get; } = new();

        public InstrumentCollector(Meter target)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, _) =>
                {
                    if (ReferenceEquals(instrument.Meter, target))
                        Instruments.Add(instrument);
                }
            };
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    [Test]
    public void ConnectionInstruments_AreCreatedWithCorrectUnits()
    {
        using var meter = new RayTreeMeter();
        using var collector = new InstrumentCollector(meter.InternalMeter);

        var disconnects = collector.Instruments.SingleOrDefault(i => i.Name == "raytree.connection.disconnects");
        var recoveries  = collector.Instruments.SingleOrDefault(i => i.Name == "raytree.connection.recoveries");
        var duration    = collector.Instruments.SingleOrDefault(i => i.Name == "raytree.connection.recovery.duration");
        var state       = collector.Instruments.SingleOrDefault(i => i.Name == "raytree.connection.state");

        Assert.That(disconnects, Is.Not.Null, "raytree.connection.disconnects missing");
        Assert.That(recoveries,  Is.Not.Null, "raytree.connection.recoveries missing");
        Assert.That(duration,    Is.Not.Null, "raytree.connection.recovery.duration missing");
        Assert.That(state,       Is.Not.Null, "raytree.connection.state missing");

        Assert.That(duration!.Unit, Is.EqualTo("s"), "duration unit must be seconds");
    }

    [Test]
    public void RecordConnectionDisconnect_EmitsCounter_WithComponentAndEndpointTags()
    {
        using var meter = new RayTreeMeter();
        using var collector = new TestMetricsCollector(meter);

        meter.RecordConnectionDisconnect("postgres.notification", "my_channel");

        var measurements = collector.Get("raytree.connection.disconnects");
        Assert.That(measurements, Has.Count.EqualTo(1));
        Assert.That(measurements[0].Value, Is.EqualTo(1));
        Assert.That(measurements[0].Tags["component"], Is.EqualTo("postgres.notification"));
        Assert.That(measurements[0].Tags["endpoint"],  Is.EqualTo("my_channel"));
    }

    [Test]
    public void RecordConnectionRecovery_Succeeded_EmitsCounterAndDuration()
    {
        using var meter = new RayTreeMeter();
        using var collector = new TestMetricsCollector(meter);

        meter.RecordConnectionRecovery(
            component: "kafka.publisher",
            endpoint: "broker:9092",
            outcome: "succeeded",
            durationSeconds: 2.5);

        var counter = collector.Get("raytree.connection.recoveries");
        Assert.That(counter, Has.Count.EqualTo(1));
        Assert.That(counter[0].Value, Is.EqualTo(1));
        Assert.That(counter[0].Tags["component"], Is.EqualTo("kafka.publisher"));
        Assert.That(counter[0].Tags["endpoint"],  Is.EqualTo("broker:9092"));
        Assert.That(counter[0].Tags["outcome"],   Is.EqualTo("succeeded"));

        var duration = collector.Get("raytree.connection.recovery.duration");
        Assert.That(duration, Has.Count.EqualTo(1));
        Assert.That(duration[0].Value, Is.EqualTo(2.5));
        Assert.That(duration[0].Tags["outcome"], Is.EqualTo("succeeded"));
    }

    [Test]
    public void RecordConnectionRecovery_Exhausted_EmitsCounterAndDuration_WithExhaustedOutcome()
    {
        using var meter = new RayTreeMeter();
        using var collector = new TestMetricsCollector(meter);

        meter.RecordConnectionRecovery(
            component: "postgres.notification",
            endpoint: "my_channel",
            outcome: "exhausted",
            durationSeconds: 10.0);

        var counter = collector.Get("raytree.connection.recoveries");
        Assert.That(counter, Has.Count.EqualTo(1));
        Assert.That(counter[0].Tags["outcome"], Is.EqualTo("exhausted"));

        var duration = collector.Get("raytree.connection.recovery.duration");
        Assert.That(duration[0].Tags["outcome"], Is.EqualTo("exhausted"));
    }

    [Test]
    public void RegisterConnectionStateGauge_ReportsConnectedState()
    {
        using var meter = new RayTreeMeter();
        using var collector = new TestMetricsCollector(meter);

        using var sub = meter.RegisterConnectionStateGauge(
            component: "rabbitmq.publisher",
            endpoint: "broker:5672",
            getState: () => 1);

        collector.RecordObservableInstruments();

        var measurements = collector.Get("raytree.connection.state");
        Assert.That(measurements, Has.Count.GreaterThanOrEqualTo(1));
        var latest = measurements[0];
        Assert.That(latest.Value, Is.EqualTo(1));
        Assert.That(latest.Tags["component"], Is.EqualTo("rabbitmq.publisher"));
        Assert.That(latest.Tags["endpoint"],  Is.EqualTo("broker:5672"));
    }

    [Test]
    public void RegisterConnectionStateGauge_ReflectsDisconnectedState()
    {
        using var meter = new RayTreeMeter();
        using var collector = new TestMetricsCollector(meter);

        var state = 1;
        using var sub = meter.RegisterConnectionStateGauge("kafka.consumer", "broker:9092", () => state);

        collector.RecordObservableInstruments();
        state = 0;
        collector.RecordObservableInstruments();

        var measurements = collector.Get("raytree.connection.state");
        // Most recent first; the second observation should be 0.
        Assert.That(measurements[0].Value, Is.EqualTo(0));
    }

    [Test]
    public void RegisterConnectionStateGauge_DisposeRemovesSource()
    {
        using var meter = new RayTreeMeter();
        using var collector = new TestMetricsCollector(meter);

        var sub = meter.RegisterConnectionStateGauge("kafka.publisher", "broker:9092", () => 1);
        collector.RecordObservableInstruments();
        var beforeDispose = collector.Get("raytree.connection.state").Count;

        sub.Dispose();
        collector.RecordObservableInstruments();
        var afterDispose = collector.Get("raytree.connection.state").Count;

        // After dispose, no new observation is recorded for this source.
        Assert.That(afterDispose, Is.EqualTo(beforeDispose));
    }

    [Test]
    public void RegisterConnectionStateGauge_GetStateThrowing_SkipsMeasurement_WithoutPropagating()
    {
        using var meter = new RayTreeMeter();
        using var collector = new TestMetricsCollector(meter);

        using var sub = meter.RegisterConnectionStateGauge(
            component: "postgres.notification",
            endpoint: "my_channel",
            getState: () => throw new InvalidOperationException("transient"));

        Assert.DoesNotThrow(() => collector.RecordObservableInstruments());
        Assert.That(collector.Get("raytree.connection.state"), Is.Empty);
    }

    [Test]
    public void RecordConnectionDisconnect_NoListener_DoesNotThrow()
    {
        using var meter = new RayTreeMeter();
        Assert.DoesNotThrow(() => meter.RecordConnectionDisconnect("kafka.publisher", "broker:9092"));
    }
}
