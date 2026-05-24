using System.Text;
using System.Threading.Channels;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Telemetry;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.RabbitMQ;

public class RabbitMqConsumer : IQueueConsumer, IDisposable
{
    private const string ComponentName = "rabbitmq.consumer";

    private readonly RabbitMqConsumerOptions _options;
    private readonly RayTreeMeter? _meter;
    private readonly string _endpoint;

    private IConnection? _connection;
    private IChannel? _channel;

    private readonly Channel<MessageEnvelope> _buffer = Channel.CreateUnbounded<MessageEnvelope>();

    // Connection state for the gauge — true while a healthy channel is bound. Owned by
    // the SDK's recovery events (we do NOT implement recovery here).
    private volatile bool _connected;
    private volatile bool _disposing;
    private DateTime _lastShutdownAt = DateTime.MinValue;
    private readonly IDisposable? _stateGaugeSubscription;

    public RabbitMqConsumer(RabbitMqConsumerOptions options, RayTreeMeter? meter = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _meter   = meter;
        _endpoint = $"{_options.HostName}:{_options.Port}";

        _stateGaugeSubscription = _meter?.RegisterConnectionStateGauge(
            component: ComponentName,
            endpoint:  _endpoint,
            getState:  () => _connected ? 1 : 0);
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

        // Subscribe to SDK recovery events for metric observability. No log output here:
        // RabbitMqConsumer intentionally has no logger (the documented exception to the
        // logging-placement rule). The metrics still record disconnect / recovery cycles.
        _connection.ConnectionShutdownAsync += OnConnectionShutdownAsync;
        _connection.RecoverySucceededAsync  += OnRecoverySucceededAsync;

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

            _connected = true;
        }
        catch
        {
            await CleanupAfterFailedInitAsync();
            throw;
        }
    }

    private Task OnConnectionShutdownAsync(object sender, ShutdownEventArgs e)
    {
        // Suppress when shutdown is part of our own teardown — same race-guard pattern
        // as the publisher (broker may already be dead when Dispose() runs, in which case
        // the SDK reports the shutdown with Initiator=Library, not Application).
        if (e.Initiator == ShutdownInitiator.Application || _disposing)
            return Task.CompletedTask;

        _connected      = false;
        _lastShutdownAt = DateTime.UtcNow;
        _meter?.RecordConnectionDisconnect(ComponentName, _endpoint);
        // No logger — silent in logs by design; metric tells the story.
        return Task.CompletedTask;
    }

    private Task OnRecoverySucceededAsync(object sender, AsyncEventArgs e)
    {
        // Clamp at zero — backward NTP-driven clock jumps would otherwise feed a negative
        // value into the duration histogram.
        var duration = _lastShutdownAt == DateTime.MinValue
            ? 0
            : Math.Max(0, (DateTime.UtcNow - _lastShutdownAt).TotalSeconds);
        _connected      = true;
        _lastShutdownAt = DateTime.MinValue;
        _meter?.RecordConnectionRecovery(ComponentName, _endpoint, outcome: "succeeded", duration);
        return Task.CompletedTask;
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
        // Mark before detaching / closing so a non-Application shutdown surfaced during
        // close (broker already dead) is suppressed by the handler's _disposing guard.
        _disposing = true;
        if (_connection is not null)
        {
            _connection.ConnectionShutdownAsync -= OnConnectionShutdownAsync;
            _connection.RecoverySucceededAsync  -= OnRecoverySucceededAsync;
        }
        _stateGaugeSubscription?.Dispose();
        _buffer.Writer.TryComplete();
        try { _channel?.CloseAsync().GetAwaiter().GetResult();    } catch { /* may already be closed */ }
        try { _connection?.CloseAsync().GetAwaiter().GetResult(); } catch { /* may already be closed */ }
        _channel?.Dispose();
        _connection?.Dispose();
    }
}
