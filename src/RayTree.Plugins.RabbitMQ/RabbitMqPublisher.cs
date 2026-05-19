using RabbitMQ.Client;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Publisher;

namespace RayTree.Plugins.RabbitMQ;

public class RabbitMqPublisher : IQueuePublisher, IDisposable
{
    private readonly RabbitMqPublisherOptions _options;
    private IConnection? _connection;
    private IChannel? _channel;

    private readonly SemaphoreSlim _semaphore = new(initialCount: 1, maxCount: 1);

    public RabbitMqPublisher(RabbitMqPublisherOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await GetChannelAsync(cancellationToken);
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
            return _channel;

        await _semaphore.WaitAsync(cancellationToken);

        try
        {
            if (_channel is { IsOpen: true })
                return _channel;

            var factory = new ConnectionFactory
            {
                HostName = _options.HostName,
                Port = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken: cancellationToken);
            _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);

            if (_options.DeclareExchange)
                await _channel.ExchangeDeclareAsync(
                    exchange: _options.ExchangeName,
                    type: _options.ExchangeType,
                    durable: _options.Durable,
                    cancellationToken: cancellationToken
                );

            return _channel;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var channel = await GetChannelAsync(cancellationToken);
        var routingKey = _options.RoutingKeySelector(envelope);

        var properties = new BasicProperties
        {
            ContentType = "application/octet-stream",
            MessageId = envelope.CorrelationId.ToString(),
            Timestamp = new AmqpTimestamp(new DateTimeOffset(envelope.Timestamp).ToUnixTimeSeconds()),
            Headers = new Dictionary<string, object?>
            {
                ["entity_type"] = envelope.EntityType,
                ["entity_id"] = envelope.EntityId,
                ["change_type"] = envelope.ChangeType.ToString(),
                ["version"] = envelope.Version
            }
        };

        await channel.BasicPublishAsync(
            exchange: _options.ExchangeName,
            routingKey: routingKey,
            mandatory: false,
            properties,
            body: envelope.Payload,
            cancellationToken: cancellationToken
        );
    }

    public void Dispose()
    {
        _channel?.CloseAsync().GetAwaiter().GetResult();
        _connection?.CloseAsync().GetAwaiter().GetResult();
        _channel?.Dispose();
        _connection?.Dispose();
        _semaphore.Dispose();
    }
}
