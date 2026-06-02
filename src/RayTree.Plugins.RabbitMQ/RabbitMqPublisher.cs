using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Publisher;

namespace RayTree.Plugins.RabbitMQ;

public class RabbitMqPublisher : IQueuePublisher, IDisposable
{
    private readonly RabbitMqPublisherOptions _options;
    private readonly ILogger<RabbitMqPublisher> _logger;
    private readonly string _endpoint;
    private IConnection? _connection;
    private IChannel? _channel;

    private readonly SemaphoreSlim _semaphore = new(initialCount: 1, maxCount: 1);

    // Timestamp of the last non-Application shutdown, used to compute the recovery
    // duration printed in the recovery log. RabbitMQ.Client owns the actual recovery
    // (AutomaticRecoveryEnabled = true by default); we only observe via logs.
    private DateTime _lastShutdownAt = DateTime.MinValue;
    // Set true from Dispose() before initiating CloseAsync so the shutdown handler can
    // suppress spurious disconnect logs even if the SDK reports the shutdown with
    // Initiator=Library (e.g. broker already dead when Dispose ran).
    private volatile bool _disposing;

    public RabbitMqPublisher(
        RabbitMqPublisherOptions options,
        ILoggerFactory?          loggerFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RabbitMqPublisher>();
        _endpoint = $"{_options.HostName}:{_options.Port}";
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

            // Hook SDK recovery events for log observability. RabbitMQ.Client owns the
            // actual recovery via AutomaticRecoveryEnabled = true (library default); we
            // only emit Warning/Information logs on the transitions.
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
        // Suppress the log when shutdown is part of our own teardown — either because
        // the SDK reports the shutdown with Initiator=Application (clean Close path) OR
        // because Dispose has begun but the broker happened to be dead first so the SDK
        // reports a non-Application initiator. _disposing covers the latter race.
        if (e.Initiator == ShutdownInitiator.Application || _disposing)
            return Task.CompletedTask;

        _lastShutdownAt = DateTime.UtcNow;
        _logger.LogWarning(
            "RabbitMQ publisher connection to {Endpoint} lost (code {ReplyCode}, {ReplyText}); library is recovering",
            _endpoint, e.ReplyCode, e.ReplyText);
        return Task.CompletedTask;
    }

    private Task OnRecoverySucceededAsync(object sender, AsyncEventArgs e)
    {
        // Clamp at zero — backward NTP-driven clock jumps would otherwise feed a negative
        // value into the duration histogram.
        var duration = _lastShutdownAt == DateTime.MinValue
            ? 0
            : Math.Max(0, (DateTime.UtcNow - _lastShutdownAt).TotalSeconds);
        _lastShutdownAt = DateTime.MinValue;
        _logger.LogInformation(
            "RabbitMQ publisher connection to {Endpoint} recovered after {Duration:F2}s",
            _endpoint, duration);
        return Task.CompletedTask;
    }

    private Task OnConnectionRecoveryErrorAsync(object sender, ConnectionRecoveryErrorEventArgs e)
    {
        // The library will keep retrying; this fires once per failed internal attempt.
        // No log beyond Information — the cycle outcome is reported by
        // OnConnectionShutdownAsync / OnRecoverySucceededAsync.
        _logger.LogInformation(e.Exception,
            "RabbitMQ publisher recovery attempt failed for {Endpoint}; library will retry",
            _endpoint);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // Mark disposal BEFORE detaching handlers and closing the connection — if the SDK
        // surfaces a non-Application shutdown during close (e.g. broker already crashed),
        // the still-attached handler short-circuits on _disposing rather than recording a
        // spurious disconnect.
        _disposing = true;

        if (_connection is not null)
        {
            _connection.ConnectionShutdownAsync       -= OnConnectionShutdownAsync;
            _connection.RecoverySucceededAsync        -= OnRecoverySucceededAsync;
            _connection.ConnectionRecoveryErrorAsync  -= OnConnectionRecoveryErrorAsync;
        }

        try { _channel?.CloseAsync().GetAwaiter().GetResult();    } catch { /* may already be closed */ }
        try { _connection?.CloseAsync().GetAwaiter().GetResult(); } catch { /* may already be closed */ }
        _channel?.Dispose();
        _connection?.Dispose();
        _semaphore.Dispose();
    }
}
