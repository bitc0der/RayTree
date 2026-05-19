using System.Text;
using Confluent.Kafka;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Publisher;

namespace RayTree.Plugins.Kafka;

public class KafkaPublisher : IQueuePublisher, IDisposable
{
    private readonly KafkaPublisherOptions _options;
    private IProducer<string, byte[]>? _producer;
    private readonly object _lock = new();

    public KafkaPublisher(KafkaPublisherOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        GetProducer();
        return Task.CompletedTask;
    }

    private IProducer<string, byte[]> GetProducer()
    {
        if (_producer != null) return _producer;

        lock (_lock)
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
    }

    public async Task PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var producer = GetProducer();

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

    public void Dispose() => _producer?.Dispose();
}
