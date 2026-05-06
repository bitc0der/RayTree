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

    public async Task PublishAsync(EntityChange change, Stream payload, CancellationToken cancellationToken = default)
    {
        var producer = GetProducer();

        using var ms = new MemoryStream();
        await payload.CopyToAsync(ms, cancellationToken);
        var body = ms.ToArray();

        var message = new Message<string, byte[]>
        {
            Key   = $"{change.EntityType}:{change.EntityId}",
            Value = body,
            Headers = new Headers
            {
                new("entity_type",    System.Text.Encoding.UTF8.GetBytes(change.EntityType)),
                new("entity_id",      System.Text.Encoding.UTF8.GetBytes(change.EntityId)),
                new("change_type",    System.Text.Encoding.UTF8.GetBytes(change.ChangeType.ToString())),
                new("correlation_id", change.CorrelationId.ToByteArray()),
                new("version",        System.Text.Encoding.UTF8.GetBytes(change.Version.ToString())),
                new("timestamp",      System.Text.Encoding.UTF8.GetBytes(change.Timestamp.ToString("O")))
            }
        };

        await producer.ProduceAsync(_options.Topic, message, cancellationToken);
    }

    public void Dispose() => _producer?.Dispose();
}
