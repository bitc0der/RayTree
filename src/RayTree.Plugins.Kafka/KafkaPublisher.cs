using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Telemetry;

namespace RayTree.Plugins.Kafka;

public class KafkaPublisher : IQueuePublisher, IDisposable
{
    private const string ComponentName = "kafka.publisher";

    private readonly KafkaPublisherOptions _options;
    private readonly ILogger<KafkaPublisher> _logger;
    private readonly RayTreeMeter? _meter;
    private readonly SemaphoreSlim _buildLock = new(initialCount: 1, maxCount: 1);
    private readonly IDisposable? _stateGaugeSubscription;

    private IProducer<string, byte[]>? _producer;

    // 0 means "healthy". Any other value is the UTC ticks of the first fatal error in the
    // current fault cycle — used as both the "rebuild requested" signal and the start of
    // the recovery-duration measurement. Interlocked-managed so the librdkafka error
    // callback (foreign thread) can flip it without any lock.
    private long _faultTicks;

    private volatile bool _disposed;

    public KafkaPublisher(
        KafkaPublisherOptions options,
        ILoggerFactory?       loggerFactory = null,
        RayTreeMeter?         meter         = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<KafkaPublisher>();
        _meter   = meter;

        _options.ConnectionRecovery.Validate();

        _stateGaugeSubscription = _meter?.RegisterConnectionStateGauge(
            component: ComponentName,
            endpoint:  _options.BootstrapServers,
            getState:  () => _producer is not null && Interlocked.Read(ref _faultTicks) == 0 ? 1 : 0);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => GetProducerAsync(cancellationToken);

    /// <summary>
    /// Returns a ready producer, lazily building (or rebuilding) under <see cref="_buildLock"/>
    /// when none exists or the most recent fatal error flagged a rebuild. The probe is re-run
    /// inside the lock on every build — initial and post-fatal — matching the
    /// kafka-topic-wait reprobe contract without an extra cache flag.
    /// </summary>
    private async Task<IProducer<string, byte[]>> GetProducerAsync(CancellationToken cancellationToken)
    {
        // Steady-state fast path: producer exists, no fault flagged. No lock acquired.
        var current = _producer;
        if (current is not null && Interlocked.Read(ref _faultTicks) == 0) return current;

        await _buildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check under the lock — a concurrent caller may have rebuilt while we waited.
            current = _producer;
            if (current is not null && Interlocked.Read(ref _faultTicks) == 0) return current;

            // Dispose any prior producer on a normal call thread (not the librdkafka callback
            // thread the error handler runs on). This is the documented-safe disposal point.
            if (_producer is { } stale)
            {
                _producer = null;
                try { stale.Dispose(); } catch { /* may already be torn down */ }
            }

            if (_options.WaitForTopic)
            {
                await KafkaTopicProbe.WaitForTopicAsync(
                    _options.BootstrapServers,
                    _options.Topic,
                    _options.TopicWaitInterval,
                    _options.TopicWaitTimeout,
                    _logger,
                    cancellationToken).ConfigureAwait(false);
            }

            _producer = new ProducerBuilder<string, byte[]>(BuildConfig())
                .SetErrorHandler(OnError)
                .Build();

            // Emit recovery metric if this build closed a fault cycle. Interlocked.Exchange
            // clears the flag and reads the original timestamp in one operation, so a
            // concurrent error handler can't lose the next disconnect event.
            var faultTicks = Interlocked.Exchange(ref _faultTicks, 0);
            if (faultTicks != 0)
            {
                var duration = (DateTime.UtcNow - new DateTime(faultTicks, DateTimeKind.Utc)).TotalSeconds;
                _meter?.RecordConnectionRecovery(ComponentName, _options.BootstrapServers,
                    outcome: "succeeded", duration);
                _logger.LogInformation(
                    "Kafka producer rebuilt for {BootstrapServers} after {Duration:F2}s",
                    _options.BootstrapServers, duration);
            }

            return _producer;
        }
        finally
        {
            try { _buildLock.Release(); }
            catch (ObjectDisposedException) { /* publisher disposed mid-init; expected at shutdown */ }
        }
    }

    private ProducerConfig BuildConfig()
    {
        var c = new ProducerConfig { BootstrapServers = _options.BootstrapServers };
        if (_options.Acks is not null)
        {
            c.Acks = _options.Acks switch
            {
                "all" => Confluent.Kafka.Acks.All,
                "1"   => Confluent.Kafka.Acks.Leader,
                "0"   => Confluent.Kafka.Acks.None,
                _     => Confluent.Kafka.Acks.All
            };
        }
        if (_options.MessageMaxBytes.HasValue) c.MessageMaxBytes = _options.MessageMaxBytes.Value;
        return c;
    }

    /// <summary>
    /// librdkafka error callback. Runs on a foreign thread; does NO locking and NO disposal —
    /// only atomic flag + metric + log. The real rebuild work happens on the next
    /// <see cref="GetProducerAsync"/> call where a normal call thread holds <see cref="_buildLock"/>.
    /// </summary>
    private void OnError(IProducer<string, byte[]> producer, Error error)
    {
        if (_disposed) return;

        if (!error.IsFatal)
        {
            _logger.LogWarning("Kafka producer non-fatal error: {Reason} (code={Code})",
                error.Reason, error.Code);
            return;
        }

        if (!_options.ConnectionRecovery.Enabled)
        {
            _logger.LogError(
                "Kafka producer fatal error: {Reason} (code={Code}); recovery disabled, producer left dead",
                error.Reason, error.Code);
            return;
        }

        // First fatal in this cycle: stamp the timestamp and emit the disconnect metric.
        // Subsequent fatals during the same cycle (before rebuild) are no-ops.
        if (Interlocked.CompareExchange(ref _faultTicks, DateTime.UtcNow.Ticks, 0) == 0)
        {
            _meter?.RecordConnectionDisconnect(ComponentName, _options.BootstrapServers);
            _logger.LogWarning(
                "Kafka producer fatal error: {Reason} (code={Code}); will rebuild on next publish",
                error.Reason, error.Code);
        }
    }

    public async Task PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var producer = await GetProducerAsync(cancellationToken).ConfigureAwait(false);

        var message = new Message<string, byte[]>
        {
            Key   = _options.KeySelector(envelope),
            Value = envelope.Payload,
            Headers = new Headers
            {
                new("entity_type",    Encoding.UTF8.GetBytes(envelope.EntityType)),
                new("entity_id",      Encoding.UTF8.GetBytes(envelope.EntityId)),
                new("change_type",    Encoding.UTF8.GetBytes(envelope.ChangeType.ToString())),
                new("correlation_id", envelope.CorrelationId.ToByteArray()),
                new("version",        Encoding.UTF8.GetBytes(envelope.Version.ToString())),
                new("timestamp",      Encoding.UTF8.GetBytes(envelope.Timestamp.ToString("O")))
            }
        };

        await producer.ProduceAsync(_options.Topic, message, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stateGaugeSubscription?.Dispose();
        _producer?.Dispose();
        _buildLock.Dispose();
    }
}
