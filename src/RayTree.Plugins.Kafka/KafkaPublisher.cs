using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Publisher;

namespace RayTree.Plugins.Kafka;

public class KafkaPublisher : IQueuePublisher, IDisposable
{
    private readonly KafkaPublisherOptions _options;
    private readonly ILogger<KafkaPublisher> _logger;
    private IProducer<string, byte[]>? _producer;

    // Tracks whether the topic-wait probe has completed successfully so we run it at most once,
    // even when InitializeAsync is bypassed and multiple PublishAsync calls race the lazy init.
    // Volatile read pattern: the probe runs under _probeSemaphore; once _probeCompleted is true,
    // subsequent callers skip the semaphore entirely.
    private volatile bool _probeCompleted;
    private readonly SemaphoreSlim _probeSemaphore = new(initialCount: 1, maxCount: 1);

    // Separate semaphore for the (very short) producer-build critical section. Splitting the
    // probe and the build means steady-state PublishAsync callers contend only on the fast
    // builder lock — they do NOT serialize behind a multi-second topic-wait probe.
    private readonly SemaphoreSlim _buildSemaphore = new(initialCount: 1, maxCount: 1);

    private volatile bool _disposed;

    public KafkaPublisher(KafkaPublisherOptions options, ILoggerFactory? loggerFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<KafkaPublisher>();
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

            _producer = new ProducerBuilder<string, byte[]>(config).Build();
            return _producer;
        }
        finally
        {
            SafeRelease(_buildSemaphore);
        }
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

        _producer?.Dispose();
        _probeSemaphore.Dispose();
        _buildSemaphore.Dispose();
    }
}
