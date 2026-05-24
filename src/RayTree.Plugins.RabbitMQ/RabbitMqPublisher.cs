using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Telemetry;

namespace RayTree.Plugins.RabbitMQ;

public class RabbitMqPublisher : IQueuePublisher, IDisposable
{
    private const string ComponentName = "rabbitmq.publisher";

    private readonly RabbitMqPublisherOptions _options;
    private readonly ILogger<RabbitMqPublisher> _logger;
    private readonly RayTreeMeter? _meter;
    private readonly string _endpoint;
    private IConnection? _connection;
    private IChannel? _channel;

    private readonly SemaphoreSlim _semaphore = new(initialCount: 1, maxCount: 1);

    // Connection state for the connection.state gauge. Set to true once GetChannelAsync
    // returns a healthy channel; flipped by the SDK's ConnectionShutdownAsync /
    // RecoverySucceededAsync events. RabbitMQ.Client owns the actual recovery (we have
    // AutomaticRecoveryEnabled = true by default); we only observe.
    private volatile bool _connected;
    private DateTime _lastShutdownAt = DateTime.MinValue;
    private readonly IDisposable? _stateGaugeSubscription;

    public RabbitMqPublisher(
        RabbitMqPublisherOptions options,
        ILoggerFactory?          loggerFactory = null,
        RayTreeMeter?            meter         = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RabbitMqPublisher>();
        _meter   = meter;
        _endpoint = $"{_options.HostName}:{_options.Port}";

        _stateGaugeSubscription = _meter?.RegisterConnectionStateGauge(
            component: ComponentName,
            endpoint:  _endpoint,
            getState:  () => _connected ? 1 : 0);
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

            // Hook SDK recovery events for metric/log observability. RabbitMQ.Client owns
            // the actual recovery via AutomaticRecoveryEnabled = true (library default);
            // we only emit raytree.connection.* on the transitions.
            _connection.ConnectionShutdownAsync       += OnConnectionShutdownAsync;
            _connection.RecoverySucceededAsync        += OnRecoverySucceededAsync;
            _connection.ConnectionRecoveryErrorAsync  += OnConnectionRecoveryErrorAsync;

            try
            {
                if (_options is { WaitForTopology: true, DeclareExchange: false })
                {
                    await TopologyProbe.WaitForExchangeAsync(
                        _connection,
                        _options.ExchangeName,
                        _options.TopologyWaitInterval,
                        _options.TopologyWaitTimeout,
                        _logger,
                        cancellationToken);
                }

                _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);

                if (_options.DeclareExchange)
                    await _channel.ExchangeDeclareAsync(
                        exchange: _options.ExchangeName,
                        type: _options.ExchangeType,
                        durable: _options.Durable,
                        cancellationToken: cancellationToken
                    );

                _connected = true;
                return _channel;
            }
            catch
            {
                await CleanupAfterFailedInitAsync();
                throw;
            }
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

    private Task OnConnectionShutdownAsync(object sender, ShutdownEventArgs e)
    {
        // Application-initiated shutdowns (Dispose/Close) are part of normal teardown,
        // not a recovery event. Skip metric + log; the connected state is irrelevant
        // because the publisher is going away.
        if (e.Initiator == ShutdownInitiator.Application)
            return Task.CompletedTask;

        _connected      = false;
        _lastShutdownAt = DateTime.UtcNow;
        _meter?.RecordConnectionDisconnect(ComponentName, _endpoint);
        _logger.LogWarning(
            "RabbitMQ publisher connection to {Endpoint} lost (code {ReplyCode}, {ReplyText}); library is recovering",
            _endpoint, e.ReplyCode, e.ReplyText);
        return Task.CompletedTask;
    }

    private Task OnRecoverySucceededAsync(object sender, AsyncEventArgs e)
    {
        var duration = _lastShutdownAt == DateTime.MinValue
            ? 0
            : (DateTime.UtcNow - _lastShutdownAt).TotalSeconds;
        _connected      = true;
        _lastShutdownAt = DateTime.MinValue;
        _meter?.RecordConnectionRecovery(ComponentName, _endpoint, outcome: "succeeded", duration);
        _logger.LogInformation(
            "RabbitMQ publisher connection to {Endpoint} recovered after {Duration:F2}s",
            _endpoint, duration);
        return Task.CompletedTask;
    }

    private Task OnConnectionRecoveryErrorAsync(object sender, ConnectionRecoveryErrorEventArgs e)
    {
        // The library will keep retrying; this fires once per failed internal attempt.
        // No metric — only the cycle outcome is counted via RecoverySucceeded/Disconnect.
        _logger.LogInformation(e.Exception,
            "RabbitMQ publisher recovery attempt failed for {Endpoint}; library will retry",
            _endpoint);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_connection is not null)
        {
            _connection.ConnectionShutdownAsync       -= OnConnectionShutdownAsync;
            _connection.RecoverySucceededAsync        -= OnRecoverySucceededAsync;
            _connection.ConnectionRecoveryErrorAsync  -= OnConnectionRecoveryErrorAsync;
        }

        _stateGaugeSubscription?.Dispose();
        _channel?.CloseAsync().GetAwaiter().GetResult();
        _connection?.CloseAsync().GetAwaiter().GetResult();
        _channel?.Dispose();
        _connection?.Dispose();
        _semaphore.Dispose();
    }
}
