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
    private IChannel? _channel;

    private readonly Channel<MessageEnvelope> _buffer = Channel.CreateUnbounded<MessageEnvelope>();

    public RabbitMqConsumer(RabbitMqConsumerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password
        };

        _connection = await factory.CreateConnectionAsync(cancellationToken);

        try
        {
            // No logger is passed to the probe: RabbitMqConsumer intentionally has no logger
            // (documented exception to the logging-placement rule in CLAUDE.md).
            if (_options is { WaitForTopology: true, DeclareQueue: false })
            {
                await TopologyProbe.WaitForQueueAsync(
                    _connection,
                    _options.QueueName,
                    _options.TopologyWaitInterval,
                    _options.TopologyWaitTimeout,
                    logger: null,
                    cancellationToken);
            }

            _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);

            if (_options.DeclareQueue)
                await _channel.QueueDeclareAsync(
                    queue: _options.QueueName,
                    durable: _options.Durable,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null,
                    cancellationToken: cancellationToken
                );

            if (!string.IsNullOrEmpty(_options.ExchangeName))
            {
                if (_options.WaitForTopology)
                {
                    await TopologyProbe.WaitForExchangeAsync(
                        _connection,
                        _options.ExchangeName,
                        _options.TopologyWaitInterval,
                        _options.TopologyWaitTimeout,
                        logger: null,
                        cancellationToken);
                }

                await _channel.QueueBindAsync(
                    queue: _options.QueueName,
                    exchange: _options.ExchangeName,
                    routingKey: _options.BindingKey,
                    cancellationToken: cancellationToken
                );
            }

            await _channel.BasicQosAsync(
                prefetchSize: 0,
                prefetchCount: _options.PrefetchCount,
                global: false,
                cancellationToken: cancellationToken
            );

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.ReceivedAsync += OnMessageReceived;

            await _channel.BasicConsumeAsync(
                queue: _options.QueueName,
                autoAck: false,
                consumer: consumer,
                cancellationToken: cancellationToken
            );
        }
        catch
        {
            await CleanupAfterFailedInitAsync();
            throw;
        }
    }

    private async Task CleanupAfterFailedInitAsync()
    {
        if (_channel is not null)
        {
            try { await _channel.CloseAsync(CancellationToken.None); } catch { /* may already be closed */ }
            _channel.Dispose();
            _channel = null;
        }

        if (_connection is not null)
        {
            try { await _connection.CloseAsync(CancellationToken.None); } catch { /* may already be closed */ }
            _connection.Dispose();
            _connection = null;
        }
    }

    private async Task OnMessageReceived(object sender, BasicDeliverEventArgs ea)
    {
        try
        {
            var envelope = ParseEnvelope(ea.BasicProperties, ea.Body.ToArray());

            if (_options.AckAfterHandler)
            {
                // Stash the delivery tag so AcknowledgeAsync / NegativeAcknowledgeAsync
                // can correlate the broker delivery back to the envelope the subscriber
                // hands us. The ACK is deferred until handler completion.
                envelope.SetDeliveryTag(ea.DeliveryTag);
                await _buffer.Writer.WriteAsync(envelope, cancellationToken: ea.CancellationToken);
            }
            else
            {
                // At-most-once (legacy default): ACK immediately after buffering.
                await _buffer.Writer.WriteAsync(envelope, cancellationToken: ea.CancellationToken);
                await _channel!.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: ea.CancellationToken);
            }
        }
        catch
        {
            await _channel!.BasicNackAsync(
                deliveryTag: ea.DeliveryTag,
                multiple: false,
                requeue: true,
                cancellationToken: ea.CancellationToken
            );
        }
    }

    public IAsyncEnumerable<MessageEnvelope> ConsumeAsync(CancellationToken cancellationToken = default)
        => _buffer.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// Sends <c>basic.ack</c> to the broker for the delivery associated with
    /// <paramref name="envelope"/>. No-op when <see cref="RabbitMqConsumerOptions.AckAfterHandler"/>
    /// is <c>false</c> (the message was already ACKed in <see cref="OnMessageReceived"/>)
    /// or when the envelope carries no delivery-tag metadata.
    /// </summary>
    public async Task AcknowledgeAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (!_options.AckAfterHandler) return;
        if (_channel is null) return;
        if (!envelope.TryTakeDeliveryTag(out var tag)) return;

        await _channel.BasicAckAsync(tag, multiple: false, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Sends <c>basic.nack</c> with <c>requeue = true</c> for the delivery associated
    /// with <paramref name="envelope"/>. No-op when <see cref="RabbitMqConsumerOptions.AckAfterHandler"/>
    /// is <c>false</c> (the message was already ACKed) or when the envelope carries no
    /// delivery-tag metadata.
    /// </summary>
    public async Task NegativeAcknowledgeAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (!_options.AckAfterHandler) return;
        if (_channel is null) return;
        if (!envelope.TryTakeDeliveryTag(out var tag)) return;

        await _channel.BasicNackAsync(tag, multiple: false, requeue: true, cancellationToken: cancellationToken);
    }

    private static MessageEnvelope ParseEnvelope(IReadOnlyBasicProperties props, byte[] body)
    {
        var headers = props.Headers ?? new Dictionary<string, object?>();

        return new MessageEnvelope
        {
            EntityType = GetHeader(headers, "entity_type"),
            EntityId = GetHeader(headers, "entity_id"),
            ChangeType = Enum.Parse<ChangeType>(GetHeader(headers, "change_type")),
            Version = int.TryParse(GetHeader(headers, "version"), out var v) ? v : 0,
            CorrelationId = Guid.TryParse(props.MessageId, out var g) ? g : Guid.Empty,
            Timestamp = props.Timestamp.UnixTime > 0
                ? DateTimeOffset.FromUnixTimeSeconds(props.Timestamp.UnixTime).UtcDateTime
                : DateTime.UtcNow,
            Payload = body
        };
    }

    private static string GetHeader(IDictionary<string, object?> headers, string key)
    {
        if (!headers.TryGetValue(key, out var value)) return string.Empty;
        return value switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string str => str,
            _ => value?.ToString() ?? string.Empty
        };
    }

    public void Dispose()
    {
        _buffer.Writer.TryComplete();
        try { _channel?.CloseAsync().GetAwaiter().GetResult();    } catch { /* may already be closed */ }
        try { _connection?.CloseAsync().GetAwaiter().GetResult(); } catch { /* may already be closed */ }
        _channel?.Dispose();
        _connection?.Dispose();
    }
}
