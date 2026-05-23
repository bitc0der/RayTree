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

    // SemaphoreSlim (not lock) so the producer-init critical section can await the async probe.
    // Mirrors RabbitMqPublisher._semaphore for the same reason.
    private readonly SemaphoreSlim _semaphore = new(initialCount: 1, maxCount: 1);

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

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_producer != null) return _producer;

            // Probe the topic BEFORE building the producer — both the InitializeAsync path
            // and the lazy PublishAsync path route through here, so the probe cannot be bypassed.
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
            _semaphore.Release();
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
        _producer?.Dispose();
        _semaphore.Dispose();
    }
}
