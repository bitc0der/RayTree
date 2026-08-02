using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Models;
using RayTree.Core.Tracking;
using Testcontainers.Kafka;

namespace RayTree.Plugins.Kafka.Tests;

/// <summary>
/// Smoke tests for the connection-recovery wiring on <see cref="KafkaPublisher"/> and
/// <see cref="KafkaConsumer"/>. Connection metrics were removed; recovery is now observed via
/// logs only. <b>These tests do not attempt to trigger librdkafka fatal errors</b> (fatal errors
/// require pre-positioned broker-side state and are not deterministically reproducible from a
/// black-box integration test). They verify that under normal operation publisher/consumer init
/// and clean dispose do not surface spurious faults, and that non-fatal transport errors against
/// an unreachable broker are logged (not treated as a fatal fault).
/// </summary>
[NonParallelizable]
public class KafkaRecoveryMetricsTests : IAsyncDisposable
{
    private readonly KafkaContainer _kafka = new KafkaBuilder("confluentinc/cp-kafka:7.7.8").Build();

    [OneTimeSetUp]
    public Task OneTimeSetUp() => _kafka.StartAsync();

    public async ValueTask DisposeAsync() => await _kafka.DisposeAsync();

    [Test]
    public async Task Publisher_AfterInitialize_PublishesCleanly()
    {
        var topic = $"recovery-smoke-{Guid.NewGuid():N}";

        using var publisher = new KafkaPublisher(new KafkaPublisherOptions
        {
            BootstrapServers = _kafka.GetBootstrapAddress(),
            Topic            = topic
        }, loggerFactory: null);

        await publisher.InitializeAsync();

        // Publish one message so the producer actually establishes a broker connection
        // (Build alone is in-memory only). Must not throw.
        Assert.DoesNotThrowAsync(() => publisher.PublishAsync(SampleEnvelope(topic)));
    }

    [Test]
    public async Task Consumer_AfterInitialize_SubscribesCleanly()
    {
        var topic = $"consumer-smoke-{Guid.NewGuid():N}";

        // Create the topic first so the consumer can subscribe cleanly.
        using (var producer = new KafkaPublisher(
            new KafkaPublisherOptions { BootstrapServers = _kafka.GetBootstrapAddress(), Topic = topic },
            loggerFactory: null))
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
        }, NullLoggerFactory.Instance);

        Assert.DoesNotThrowAsync(() => consumer.InitializeAsync());
    }

    [Test]
    public async Task Publisher_UnreachableBroker_LogsNonFatalError_NoRebuild()
    {
        // Point at a port that has nothing listening. librdkafka emits non-fatal
        // transport errors continuously while trying to bootstrap. The publisher SHALL NOT
        // treat these as a fault — _faultTicks stays 0, no rebuild. This verifies the
        // `!error.IsFatal` short-circuit in OnError via the Warning log it emits.
        var logs = new WarningCountingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Warning));

        using var publisher = new KafkaPublisher(new KafkaPublisherOptions
        {
            BootstrapServers = "127.0.0.1:1",   // nothing here
            Topic            = "unreachable-test"
        }, loggerFactory: loggerFactory);

        await publisher.InitializeAsync();

        // Wait until OnError has fired at least once with a non-fatal error.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (logs.NonFatalErrors == 0 && sw.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(50);
        Assert.That(logs.NonFatalErrors, Is.GreaterThanOrEqualTo(1),
            "librdkafka SHOULD emit at least one non-fatal error against an unreachable broker");
    }

    /// <summary>
    /// Minimal logger provider that counts Warning entries containing "non-fatal" — used by
    /// the unreachable-broker test to poll until librdkafka has actually surfaced an error
    /// rather than burning a fixed delay.
    /// </summary>
    private sealed class WarningCountingLoggerProvider : ILoggerProvider
    {
        private int _nonFatal;
        public int NonFatalErrors => Volatile.Read(ref _nonFatal);
        public ILogger CreateLogger(string categoryName) => new CountingLogger(this);
        public void Dispose() { }

        private sealed class CountingLogger : ILogger
        {
            private readonly WarningCountingLoggerProvider _owner;
            public CountingLogger(WarningCountingLoggerProvider owner) { _owner = owner; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
                Func<TState, Exception?, string> formatter)
            {
                if (level == LogLevel.Warning && formatter(state, ex).Contains("non-fatal", StringComparison.OrdinalIgnoreCase))
                    Interlocked.Increment(ref _owner._nonFatal);
            }
        }
    }

    [Test]
    public async Task Publisher_CleanDispose_DoesNotThrow()
    {
        var topic = $"dispose-smoke-{Guid.NewGuid():N}";

        var publisher = new KafkaPublisher(new KafkaPublisherOptions
        {
            BootstrapServers = _kafka.GetBootstrapAddress(),
            Topic            = topic
        }, loggerFactory: null);

        await publisher.InitializeAsync();
        await publisher.PublishAsync(SampleEnvelope(topic));

        Assert.DoesNotThrow(() => publisher.Dispose());
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
}
