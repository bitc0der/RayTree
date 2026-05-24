using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Models;
using RayTree.Core.Telemetry;
using RayTree.Core.Tracking;
using Testcontainers.Kafka;

namespace RayTree.Plugins.Kafka.Tests;

/// <summary>
/// Smoke tests for the connection-recovery metric wiring on <see cref="KafkaPublisher"/> and
/// <see cref="KafkaConsumer"/>. <b>These tests do not attempt to trigger librdkafka fatal
/// errors</b> — see task 7.1 / 7.2 in <c>tasks.md</c> for the rationale (fatal errors require
/// pre-positioned broker-side state and are not deterministically reproducible from a black-box
/// integration test). They verify that under normal operation:
///
/// 1. The connection-state gauge correctly reports 1 (connected) after init.
/// 2. No spurious disconnect metrics are emitted during clean operation.
/// 3. Application-initiated dispose does not register as a recovery event.
///
/// The fatal-error path itself is verified by code review against the spec; the metric helpers
/// are exercised by <c>RecoveryMetricsTests</c> in the Core test project.
/// </summary>
[NonParallelizable]
public class KafkaRecoveryMetricsTests : IAsyncDisposable
{
    private readonly KafkaContainer _kafka = new KafkaBuilder("confluentinc/cp-kafka:7.7.8").Build();
    private RayTreeMeter _meter = null!;
    private CapturingMeterListener _capture = null!;

    [OneTimeSetUp]
    public Task OneTimeSetUp() => _kafka.StartAsync();

    [SetUp]
    public void SetUp()
    {
        _meter = new RayTreeMeter();
        _capture = new CapturingMeterListener();
    }

    [TearDown]
    public void TearDown()
    {
        _capture.Dispose();
        _meter.Dispose();
    }

    public async ValueTask DisposeAsync() => await _kafka.DisposeAsync();

    [Test]
    public async Task Publisher_AfterInitialize_StateGaugeReports1_NoSpuriousDisconnects()
    {
        var topic = $"recovery-smoke-{Guid.NewGuid():N}";

        using var publisher = new KafkaPublisher(new KafkaPublisherOptions
        {
            BootstrapServers = _kafka.GetBootstrapAddress(),
            Topic            = topic
        }, loggerFactory: null, meter: _meter);

        await publisher.InitializeAsync();

        // Publish one message so the producer actually establishes a broker connection
        // (Build alone is in-memory only).
        await publisher.PublishAsync(SampleEnvelope(topic));

        // Force the OTel gauge callback to fire so the meter listener sees a state observation.
        _capture.RecordObservableInstruments();

        Assert.Multiple(() =>
        {
            Assert.That(_capture.LatestGaugeValue("raytree.connection.state", "kafka.publisher"),
                Is.EqualTo(1), "state gauge SHOULD report 1 (connected) after a successful publish");
            Assert.That(_capture.SumOf("raytree.connection.disconnects", "kafka.publisher"),
                Is.EqualTo(0), "no disconnect should fire during clean operation");
            Assert.That(_capture.SumOf("raytree.connection.recoveries", "kafka.publisher"),
                Is.EqualTo(0), "no recovery should fire when no fault occurred");
        });
    }

    [Test]
    public async Task Consumer_AfterInitialize_StateGaugeReports1_NoSpuriousDisconnects()
    {
        var topic = $"consumer-smoke-{Guid.NewGuid():N}";

        // Create the topic first so the consumer can subscribe cleanly.
        using (var producer = new KafkaPublisher(
            new KafkaPublisherOptions { BootstrapServers = _kafka.GetBootstrapAddress(), Topic = topic },
            loggerFactory: null, meter: null))
        {
            await producer.InitializeAsync();
            await producer.PublishAsync(SampleEnvelope(topic));
        }

        using var consumer = new KafkaConsumer(new KafkaConsumerOptions
        {
            BootstrapServers = _kafka.GetBootstrapAddress(),
            Topic            = topic,
            GroupId          = $"smoke-group-{Guid.NewGuid():N}",
            PollTimeoutMs    = 200
        }, NullLoggerFactory.Instance, meter: _meter);

        await consumer.InitializeAsync();
        _capture.RecordObservableInstruments();

        Assert.Multiple(() =>
        {
            Assert.That(_capture.LatestGaugeValue("raytree.connection.state", "kafka.consumer"),
                Is.EqualTo(1), "state gauge SHOULD report 1 after successful Subscribe");
            Assert.That(_capture.SumOf("raytree.connection.disconnects", "kafka.consumer"),
                Is.EqualTo(0));
            Assert.That(_capture.SumOf("raytree.connection.recoveries", "kafka.consumer"),
                Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Publisher_UnreachableBroker_NoFatalDisconnect_NoRebuild()
    {
        // Point at a port that has nothing listening. librdkafka emits non-fatal
        // transport errors continuously while trying to bootstrap. The publisher SHALL NOT
        // treat these as a fault — _faultTicks stays 0, no disconnect counter, no rebuild.
        // This verifies the `!error.IsFatal` short-circuit in OnError.
        using var publisher = new KafkaPublisher(new KafkaPublisherOptions
        {
            BootstrapServers = "127.0.0.1:1",   // nothing here
            Topic            = "unreachable-test"
        }, loggerFactory: null, meter: _meter);

        await publisher.InitializeAsync();
        // Give librdkafka time to attempt the bootstrap connection and emit several
        // non-fatal `Local_AllBrokersDown` / `Local_Transport` errors.
        await Task.Delay(2_000);

        Assert.That(_capture.SumOf("raytree.connection.disconnects", "kafka.publisher"), Is.EqualTo(0),
            "non-fatal transient errors MUST NOT increment the disconnect counter");
        Assert.That(_capture.SumOf("raytree.connection.recoveries", "kafka.publisher"), Is.EqualTo(0),
            "without a fatal error there's nothing to recover from — no recovery should be emitted");
    }

    [Test]
    public async Task Publisher_CleanDispose_DoesNotEmitDisconnectOrRecovery()
    {
        var topic = $"dispose-smoke-{Guid.NewGuid():N}";

        var publisher = new KafkaPublisher(new KafkaPublisherOptions
        {
            BootstrapServers = _kafka.GetBootstrapAddress(),
            Topic            = topic
        }, loggerFactory: null, meter: _meter);

        await publisher.InitializeAsync();
        await publisher.PublishAsync(SampleEnvelope(topic));

        publisher.Dispose();
        await Task.Delay(500);   // give any pending callbacks a moment to fire

        Assert.That(_capture.SumOf("raytree.connection.disconnects", "kafka.publisher"), Is.EqualTo(0),
            "application-initiated dispose must not register as a disconnect");
        Assert.That(_capture.SumOf("raytree.connection.recoveries", "kafka.publisher"), Is.EqualTo(0));
    }

    // ---- helpers --------------------------------------------------------

    private static MessageEnvelope SampleEnvelope(string _) => new()
    {
        EntityType    = "RayTree.Plugins.Kafka.Tests.Order",
        EntityId      = "1",
        ChangeType    = ChangeType.Insert,
        CorrelationId = Guid.NewGuid(),
        Version       = 1,
        Timestamp     = DateTime.UtcNow,
        Payload       = new byte[] { 0x01, 0x02, 0x03 }
    };

    private sealed class CapturingMeterListener : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<Recorded> _measurements = new();
        private readonly object _gate = new();

        public CapturingMeterListener()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == RayTreeMeter.MeterName)
                        l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>  ((i, v, t, _) => Append(i, v, t));
            _listener.SetMeasurementEventCallback<int>   ((i, v, t, _) => Append(i, v, t));
            _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Append(i, v, t));
            _listener.Start();
        }

        private void Append(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string? component = null;
            foreach (var kv in tags)
                if (kv.Key == "component") { component = kv.Value as string; break; }
            lock (_gate) _measurements.Add(new Recorded(instrument.Name, value, component));
        }

        public void RecordObservableInstruments() => _listener.RecordObservableInstruments();

        public double SumOf(string name, string component)
        {
            lock (_gate)
                return _measurements.Where(m => m.Name == name && m.Component == component).Sum(m => m.Value);
        }

        public double? LatestGaugeValue(string name, string component)
        {
            lock (_gate)
            {
                var match = _measurements.LastOrDefault(m => m.Name == name && m.Component == component);
                return match.Name is null ? null : match.Value;
            }
        }

        public void Dispose() => _listener.Dispose();

        private readonly record struct Recorded(string Name, double Value, string? Component);
    }
}
