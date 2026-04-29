using System.IO.Pipelines;
using Confluent.Kafka;
using RayTree.Models;
using RayTree.Plugins;

namespace RayTree.Plugins.Kafka;

public class KafkaPublisherOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string Topic { get; set; } = "entity_changes";
    public string? Acks { get; set; }
    public int? MessageMaxBytes { get; set; }
}

public class KafkaPublisher : IQueuePublisher, IDisposable
{
    private readonly KafkaPublisherOptions _options;
    private IProducer<string, byte[]>? _producer;
    private readonly object _lock = new();

    public KafkaPublisher(KafkaPublisherOptions options)
    {
        _options = options;
    }

    private IProducer<string, byte[]> GetProducer()
    {
        if (_producer != null)
            return _producer;

        lock (_lock)
        {
            if (_producer != null)
                return _producer;

            var config = new ProducerConfig
            {
                BootstrapServers = _options.BootstrapServers
            };

            if (_options.Acks != null)
            {
                config.Acks = _options.Acks switch
                {
                    "all" => Confluent.Kafka.Acks.All,
                    "1" => Confluent.Kafka.Acks.Leader,
                    "0" => Confluent.Kafka.Acks.None,
                    _ => Confluent.Kafka.Acks.All
                };
            }

            if (_options.MessageMaxBytes.HasValue)
                config.MessageMaxBytes = _options.MessageMaxBytes.Value;

            var builder = new ProducerBuilder<string, byte[]>(config);
            _producer = builder.Build();

            return _producer;
        }
    }

    public async Task PublishAsync(EntityChange change, PipeReader payload, CancellationToken cancellationToken = default)
    {
        var producer = GetProducer();

        var body = await ReadPipeAsync(payload, cancellationToken);

        var message = new Message<string, byte[]>
        {
            Key = $"{change.EntityType}:{change.EntityId}",
            Value = body,
            Headers = new Headers
            {
                new("entity_type", System.Text.Encoding.UTF8.GetBytes(change.EntityType)),
                new("entity_id", System.Text.Encoding.UTF8.GetBytes(change.EntityId)),
                new("change_type", System.Text.Encoding.UTF8.GetBytes(change.ChangeType.ToString())),
                new("correlation_id", change.CorrelationId.ToByteArray()),
                new("version", System.Text.Encoding.UTF8.GetBytes(change.Version.ToString())),
                new("timestamp", System.Text.Encoding.UTF8.GetBytes(change.Timestamp.ToString("O")))
            }
        };

        await producer.ProduceAsync(_options.Topic, message, cancellationToken);
    }

    private static async Task<byte[]> ReadPipeAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        var result = await reader.ReadAsync(cancellationToken);
        var buffer = result.Buffer;

        while (!result.IsCompleted)
        {
            foreach (var segment in buffer)
            {
                await ms.WriteAsync(segment, cancellationToken);
            }
            reader.AdvanceTo(buffer.End);
            result = await reader.ReadAsync(cancellationToken);
            buffer = result.Buffer;
        }

        if (!buffer.IsEmpty)
        {
            foreach (var segment in buffer)
            {
                await ms.WriteAsync(segment, cancellationToken);
            }
            reader.AdvanceTo(buffer.End);
        }

        await reader.CompleteAsync();
        return ms.ToArray();
    }

    public void Dispose()
    {
        _producer?.Dispose();
    }
}
