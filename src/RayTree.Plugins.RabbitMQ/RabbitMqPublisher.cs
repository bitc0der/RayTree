using System.IO.Pipelines;
using RabbitMQ.Client;
using RayTree.Models;
using RayTree.Plugins;

namespace RayTree.Plugins.RabbitMQ;

public class RabbitMqPublisherOptions
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string ExchangeName { get; set; } = "entity_changes";
    public string RoutingKey { get; set; } = "change";
    public bool DeclareExchange { get; set; } = true;
    public string ExchangeType { get; set; } = "topic";
    public bool Durable { get; set; } = true;
}

public class RabbitMqPublisher : IQueuePublisher, IDisposable
{
    private readonly RabbitMqPublisherOptions _options;
    private IConnection? _connection;
    private IModel? _channel;
    private readonly object _lock = new();

    public RabbitMqPublisher(RabbitMqPublisherOptions options)
    {
        _options = options;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var channel = GetChannel();
        return Task.CompletedTask;
    }

    private IModel GetChannel()
    {
        if (_channel != null && _channel.IsOpen)
            return _channel;

        lock (_lock)
        {
            if (_channel != null && _channel.IsOpen)
                return _channel;

            var factory = new ConnectionFactory
            {
                HostName = _options.HostName,
                Port = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password
            };

            _connection = factory.CreateConnection();
            _channel = _connection.CreateModel();

            if (_options.DeclareExchange)
            {
                _channel.ExchangeDeclare(_options.ExchangeName, _options.ExchangeType, _options.Durable);
            }

            return _channel;
        }
    }

    public async Task PublishAsync(EntityChange change, PipeReader payload, CancellationToken cancellationToken = default)
    {
        var channel = GetChannel();

        var body = await ReadPipeAsync(payload, cancellationToken);
        var routingKey = $"{_options.RoutingKey}.{change.EntityType}.{change.ChangeType.ToString().ToLower()}";

        var properties = channel.CreateBasicProperties();
        properties.ContentType = "application/octet-stream";
        properties.MessageId = change.CorrelationId.ToString();
        properties.Timestamp = new AmqpTimestamp((long)new DateTimeOffset(change.Timestamp).ToUnixTimeSeconds());
        properties.Headers = new Dictionary<string, object?>
        {
            ["entity_type"] = change.EntityType,
            ["entity_id"] = change.EntityId,
            ["change_type"] = change.ChangeType.ToString(),
            ["version"] = change.Version
        };

        channel.BasicPublish(_options.ExchangeName, routingKey, false, properties, body);
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
        _channel?.Close();
        _connection?.Close();
        _channel?.Dispose();
        _connection?.Dispose();
    }
}
