using RabbitMQ.Client;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Publisher;

namespace RayTree.Plugins.RabbitMQ;

public class RabbitMqPublisher : IQueuePublisher, IDisposable
{
    private readonly RabbitMqPublisherOptions _options;
    private IConnection? _connection;
    private IModel? _channel;
    private readonly object _lock = new();

    public RabbitMqPublisher(RabbitMqPublisherOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        GetChannel();
        return Task.CompletedTask;
    }

    private IModel GetChannel()
    {
        if (_channel is { IsOpen: true })
            return _channel;

        lock (_lock)
        {
            if (_channel is { IsOpen: true })
                return _channel;

            var factory = new ConnectionFactory
            {
                HostName = _options.HostName,
                Port     = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password
            };

            _connection = factory.CreateConnection();
            _channel    = _connection.CreateModel();

            if (_options.DeclareExchange)
                _channel.ExchangeDeclare(_options.ExchangeName, _options.ExchangeType, _options.Durable);

            return _channel;
        }
    }

    public Task PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var channel    = GetChannel();
        var routingKey = $"{_options.RoutingKey}.{envelope.EntityType}.{envelope.ChangeType.ToString().ToLower()}";

        var properties = channel.CreateBasicProperties();
        properties.ContentType = "application/octet-stream";
        properties.MessageId   = envelope.CorrelationId.ToString();
        properties.Timestamp   = new AmqpTimestamp((long)new DateTimeOffset(envelope.Timestamp).ToUnixTimeSeconds());
        properties.Headers     = new Dictionary<string, object?>
        {
            ["entity_type"] = envelope.EntityType,
            ["entity_id"]   = envelope.EntityId,
            ["change_type"] = envelope.ChangeType.ToString(),
            ["version"]     = envelope.Version
        };

        channel.BasicPublish(_options.ExchangeName, routingKey, false, properties, envelope.Payload);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _channel?.Close();
        _connection?.Close();
        _channel?.Dispose();
        _connection?.Dispose();
    }
}
