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
    private IProducer<string, byte[]>? _producer;

    // Tracks whether the topic-wait probe has completed successfully so we run it at most once
    // per producer lifetime. Reset to false when the producer is disposed on a fatal error so
    // the rebuilt producer re-runs the probe — matching the kafka-topic-wait reprobe contract.
    private volatile bool _probeCompleted;
    private readonly SemaphoreSlim _probeSemaphore = new(initialCount: 1, maxCount: 1);
    private readonly SemaphoreSlim _buildSemaphore = new(initialCount: 1, maxCount: 1);

    // Fatal-error fault cycle tracking. default(DateTime) means "not in a fault cycle";
    // any other value is the timestamp of the first fatal error since the last recovery.
    // Read/written under _buildSemaphore.
    private DateTime _faultStartedAt;

    private readonly IDisposable? _stateGaugeSubscription;
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

        // Connection state gauge (1 = healthy producer, 0 = no producer or in a fault cycle).
        _stateGaugeSubscription = _meter?.RegisterConnectionStateGauge(
            component: ComponentName,
            endpoint:  _options.BootstrapServers,
            getState:  () => _producer is not null && _faultStartedAt == default ? 1 : 0);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await GetProducerAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IProducer<string, byte[]>> GetProducerAsync(CancellationToken cancellationToken)
    {
        if (_producer != null) return _producer;

        // Step 1: ensure the probe has completed (separate critical section). Concurrent
        // first-time callers serialize on _probeSemaphore for the probe duration. Once
        // _probeCompleted flips to true it short-circuits this entirely on every subsequent
        // call — steady-state callers never enter the probe semaphore.
        if (_options.WaitForTopic && !_probeCompleted)
        {
            await _probeSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_probeCompleted)
                {
                    await KafkaTopicProbe.WaitForTopicAsync(
                        _options.BootstrapServers,
                        _options.Topic,
                        _options.TopicWaitInterval,
                        _options.TopicWaitTimeout,
                        _logger,
                        cancellationToken).ConfigureAwait(false);
                    _probeCompleted = true;
                }
            }
            finally
            {
                SafeRelease(_probeSemaphore);
            }
        }

        // Step 2: build the producer under a separate short-lived lock. The cold-start delay
        // seen by concurrent callers is bounded to the synchronous ProducerBuilder.Build call
        // (microseconds), not the probe duration.
        await _buildSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_producer != null) return _producer;

            var config = new ProducerConfig { BootstrapServers = _options.BootstrapServers };

            if (_options.Acks != null)
            {
                config.Acks = _options.Acks switch
                {
                    "all" => Confluent.Kafka.Acks.All,
                    "1"   => Confluent.Kafka.Acks.Leader,
                    "0"   => Confluent.Kafka.Acks.None,
                    _     => Confluent.Kafka.Acks.All
                };
            }

            if (_options.MessageMaxBytes.HasValue)
                config.MessageMaxBytes = _options.MessageMaxBytes.Value;

            _producer = new ProducerBuilder<string, byte[]>(config)
                .SetErrorHandler(OnProducerError)
                .Build();

            // If we were rebuilding after a fatal-error disposal, emit the recovery metric
            // and clear the fault timestamp. First-build (no prior fault) is a no-op.
            if (_faultStartedAt != default)
            {
                var duration = (DateTime.UtcNow - _faultStartedAt).TotalSeconds;
                _meter?.RecordConnectionRecovery(ComponentName, _options.BootstrapServers,
                    outcome: "succeeded", duration);
                _logger.LogInformation(
                    "Kafka producer rebuilt for {BootstrapServers} after {Duration:F2}s",
                    _options.BootstrapServers, duration);
                _faultStartedAt = default;
            }

            return _producer;
        }
        finally
        {
            SafeRelease(_buildSemaphore);
        }
    }

    /// <summary>
    /// librdkafka error handler. Non-fatal errors (transient broker issues) are logged at
    /// <c>Warning</c> only — librdkafka recovers those internally. Fatal errors poison the
    /// native handle: we dispose the producer, reset the probe flag, and emit the disconnect
    /// metric. The next <see cref="PublishAsync"/> rebuilds via the existing lazy path.
    /// </summary>
    private void OnProducerError(IProducer<string, byte[]> producer, Error error)
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
            _logger.LogError("Kafka producer fatal error: {Reason} (code={Code}); recovery disabled, producer left dead",
                error.Reason, error.Code);
            return;
        }

        // Acquire _buildSemaphore synchronously — error handler runs on a librdkafka thread
        // and cannot await. The critical section is microseconds (field swaps + dispose).
        _buildSemaphore.Wait();
        try
        {
            if (_disposed) return;
            var producerToDispose = _producer;
            _producer       = null;
            _probeCompleted = false;
            _faultStartedAt = DateTime.UtcNow;
            try { producerToDispose?.Dispose(); } catch { /* may already be torn down */ }
        }
        finally
        {
            SafeRelease(_buildSemaphore);
        }

        _meter?.RecordConnectionDisconnect(ComponentName, _options.BootstrapServers);
        _logger.LogWarning(
            "Kafka producer fatal error: {Reason} (code={Code}); disposed, will rebuild on next publish",
            error.Reason, error.Code);
    }

    /// <summary>
    /// Release a semaphore while tolerating a concurrent <see cref="Dispose"/>. Without
    /// this, a Dispose-during-Init race would throw <see cref="ObjectDisposedException"/>
    /// out of the caller's finally block during host shutdown — noise that masks the real
    /// cancellation/shutdown signal.
    /// </summary>
    private static void SafeRelease(SemaphoreSlim semaphore)
    {
        try { semaphore.Release(); }
        catch (ObjectDisposedException) { /* publisher was disposed mid-init; expected during shutdown */ }
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
        _probeSemaphore.Dispose();
        _buildSemaphore.Dispose();
    }
}
