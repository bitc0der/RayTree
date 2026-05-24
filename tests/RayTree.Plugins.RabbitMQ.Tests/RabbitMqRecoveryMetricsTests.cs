using System.Diagnostics.Metrics;
using RayTree.Core.Telemetry;
using Testcontainers.RabbitMq;

namespace RayTree.Plugins.RabbitMQ.Tests;

/// <summary>
/// Verifies the RabbitMQ plugin's event-hook observability surface. RayTree does NOT own
/// the recovery code for RabbitMQ — <c>AutomaticRecoveryEnabled = true</c> (library default)
/// performs the rebuild. The plugin subscribes to <c>ConnectionShutdownAsync</c> /
/// <c>RecoverySucceededAsync</c> / <c>ConnectionRecoveryErrorAsync</c> to emit the shared
/// <c>raytree.connection.*</c> metrics. These tests exercise that wire-up against a real broker.
/// </summary>
[NonParallelizable]
public class RabbitMqRecoveryMetricsTests : IAsyncDisposable
{
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4.3.0-alpine").Build();
    private RayTreeMeter _meter = null!;
    private CapturingMeterListener _capture = null!;

    [OneTimeSetUp]
    public Task OneTimeSetUp() => _rabbitMq.StartAsync();

    [SetUp]
    public void SetUp()
    {
        _meter = new RayTreeMeter();
        _capture = new CapturingMeterListener(_meter);
    }

    [TearDown]
    public void TearDown()
    {
        _capture.Dispose();
        _meter.Dispose();
    }

    public async ValueTask DisposeAsync() => await _rabbitMq.DisposeAsync();

    private RabbitMqPublisher BuildPublisher() => new(new RabbitMqPublisherOptions
    {
        HostName        = _rabbitMq.Hostname,
        Port            = _rabbitMq.GetMappedPublicPort(5672),
        UserName        = RabbitMqBuilder.DefaultUsername,
        Password        = RabbitMqBuilder.DefaultPassword,
        ExchangeName    = "recovery_metrics_test",
        ExchangeType    = "topic",
        DeclareExchange = true
    }, loggerFactory: null, meter: _meter);

    private RabbitMqConsumer BuildConsumer() => new(new RabbitMqConsumerOptions
    {
        HostName     = _rabbitMq.Hostname,
        Port         = _rabbitMq.GetMappedPublicPort(5672),
        UserName     = RabbitMqBuilder.DefaultUsername,
        Password     = RabbitMqBuilder.DefaultPassword,
        QueueName    = $"recovery_q_{Guid.NewGuid():N}",
        DeclareQueue = true
    }, meter: _meter);

    [Test]
    public async Task BrokerForcesConnectionClose_EmitsDisconnect_ThenRecoverySucceeded()
    {
        // We can't simply Stop+Start the container — Testcontainers reassigns the mapped
        // public port on restart, so the library auto-recovery (which holds the original
        // host:port) has nothing to reconnect to. Instead, ask the broker itself to close
        // the AMQP connections via `rabbitmqctl close_all_connections`. The TCP connection
        // dies (library observes ConnectionShutdownAsync with non-Application initiator),
        // the broker stays up on the same port, the SDK reconnects, and
        // RecoverySucceededAsync fires.

        using var publisher = BuildPublisher();
        await publisher.InitializeAsync();

        using var consumer = BuildConsumer();
        await consumer.InitializeAsync();

        // Give the SDK a moment to settle (RabbitMqConsumer kicks off BasicConsumeAsync
        // asynchronously after InitializeAsync returns).
        await Task.Delay(500);

        // Act — broker-driven close. The 'test' string is the rabbitmqctl reason.
        var result = await _rabbitMq.ExecAsync(new[] {
            "rabbitmqctl", "close_all_connections", "raytree-recovery-test"
        });
        Assert.That(result.ExitCode, Is.EqualTo(0), $"rabbitmqctl exit code: stderr={result.Stderr}");

        // Assert — disconnect is observed on both ends, recovery follows.
        await WaitForAsync(
            () => _capture.SumOf("raytree.connection.disconnects", "rabbitmq.publisher") >= 1
               && _capture.SumOf("raytree.connection.disconnects", "rabbitmq.consumer")  >= 1,
            TimeSpan.FromSeconds(30),
            "publisher and consumer disconnect metrics");

        await WaitForAsync(
            () => _capture.SumOf("raytree.connection.recoveries", "rabbitmq.publisher", "succeeded") >= 1
               && _capture.SumOf("raytree.connection.recoveries", "rabbitmq.consumer",  "succeeded") >= 1,
            TimeSpan.FromSeconds(60),
            "publisher and consumer recovery metrics");
    }

    [Test]
    public async Task ApplicationInitiatedDispose_DoesNotEmitDisconnect()
    {
        // Arrange
        var publisher = BuildPublisher();
        await publisher.InitializeAsync();

        // Act — clean dispose; the SDK fires ConnectionShutdownAsync with Initiator=Application.
        publisher.Dispose();
        await Task.Delay(500);   // give the shutdown event time to fire

        // Assert — the publisher MUST NOT count its own clean teardown as a recovery event.
        // (Otherwise dashboards would show a spurious disconnect every time a host shuts down.)
        Assert.That(_capture.SumOf("raytree.connection.disconnects", "rabbitmq.publisher"), Is.EqualTo(0),
            "ConnectionShutdownAsync with Initiator=Application must not emit disconnect");
    }

    // ---- helpers --------------------------------------------------------

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return;
            await Task.Delay(200);
        }
        throw new TimeoutException($"Timed out waiting for: {what}");
    }

    private sealed class CapturingMeterListener : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<Recorded> _measurements = new();
        private readonly object _gate = new();

        public CapturingMeterListener(RayTreeMeter meter)
        {
            _ = meter;   // accepted for future scoping; we filter by meter name (public)
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
            string? component = null, outcome = null;
            foreach (var kv in tags)
            {
                if      (kv.Key == "component") component = kv.Value as string;
                else if (kv.Key == "outcome")   outcome   = kv.Value as string;
            }
            lock (_gate) _measurements.Add(new Recorded(instrument.Name, value, component, outcome));
        }

        public double SumOf(string instrumentName, string component, string? outcome = null)
        {
            lock (_gate)
                return _measurements
                    .Where(m => m.Name == instrumentName && m.Component == component
                                && (outcome is null || m.Outcome == outcome))
                    .Sum(m => m.Value);
        }

        public void Dispose() => _listener.Dispose();

        private readonly record struct Recorded(string Name, double Value, string? Component, string? Outcome);
    }
}
