using System.Text;
using System.Threading.Channels;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.RabbitMQ;

public class RabbitMqConsumer : IQueueConsumer, IDisposable
{
    private readonly RabbitMqConsumerOptions _options;
    private IConnection? _connection;
    private IModel? _channel;
    private readonly Channel<(EntityChange Change, byte[] Payload)> _buffer =
        Channel.CreateUnbounded<(EntityChange, byte[])>();

    public RabbitMqConsumer(RabbitMqConsumerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var factory = new ConnectionFactory
        {
            HostName               = _options.HostName,
            Port                   = _options.Port,
            UserName               = _options.UserName,
            Password               = _options.Password,
            DispatchConsumersAsync = true
        };

        _connection = factory.CreateConnection();
        _channel    = _connection.CreateModel();

        if (_options.DeclareQueue)
            _channel.QueueDeclare(_options.QueueName, durable: _options.Durable,
                exclusive: false, autoDelete: false, arguments: null);

        if (!string.IsNullOrEmpty(_options.ExchangeName))
            _channel.QueueBind(_options.QueueName, _options.ExchangeName, _options.BindingKey);

        _channel.BasicQos(prefetchSize: 0, prefetchCount: _options.PrefetchCount, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += OnMessageReceived;

        _channel.BasicConsume(_options.QueueName, autoAck: false, consumer: consumer);
        return Task.CompletedTask;
    }

    private async Task OnMessageReceived(object sender, BasicDeliverEventArgs ea)
    {
        try
        {
            var change  = ParseEntityChange(ea.BasicProperties);
            var payload = ea.Body.ToArray();
            await _buffer.Writer.WriteAsync((change, payload));
            _channel!.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch
        {
            _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: true);
        }
    }

    public IAsyncEnumerable<(EntityChange Change, byte[] Payload)> ConsumeAsync(
        CancellationToken cancellationToken = default)
        => _buffer.Reader.ReadAllAsync(cancellationToken);

    private static EntityChange ParseEntityChange(IBasicProperties props)
    {
        var headers = props.Headers ?? new Dictionary<string, object?>();

        return new EntityChange
        {
            EntityType    = GetHeader(headers, "entity_type"),
            EntityId      = GetHeader(headers, "entity_id"),
            ChangeType    = Enum.Parse<ChangeType>(GetHeader(headers, "change_type")),
            Version       = int.TryParse(GetHeader(headers, "version"), out var v) ? v : 0,
            CorrelationId = Guid.TryParse(props.MessageId, out var g) ? g : Guid.Empty,
            Timestamp     = props.Timestamp.UnixTime > 0
                ? DateTimeOffset.FromUnixTimeSeconds(props.Timestamp.UnixTime).UtcDateTime
                : DateTime.UtcNow
        };
    }

    private static string GetHeader(IDictionary<string, object?> headers, string key)
    {
        if (!headers.TryGetValue(key, out var value)) return string.Empty;
        return value switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string str   => str,
            _            => value?.ToString() ?? string.Empty
        };
    }

    public void Dispose()
    {
        _buffer.Writer.TryComplete();
        _channel?.Close();
        _connection?.Close();
        _channel?.Dispose();
        _connection?.Dispose();
    }
}
