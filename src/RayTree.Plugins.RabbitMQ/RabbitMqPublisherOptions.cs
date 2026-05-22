using RayTree.Core.Models;

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

    /// <summary>
    /// Selects the AMQP routing key for each outgoing message.
    /// On a <c>topic</c> exchange, consumers bind queues with wildcard patterns
    /// (e.g. <c>change.Order.*</c> or <c>change.*.insert</c>) to receive only the
    /// messages they care about — this is RabbitMQ's primary routing and parallelism primitive.
    /// <para>
    /// Defaults to <c>"{RoutingKey}.{EntityType}.{changeType}"</c> (e.g. <c>change.Order.insert</c>),
    /// reading <see cref="RoutingKey"/> at call time so changes to that property are always reflected.
    /// Override to route by tenant, aggregate root, or any value derivable from the envelope.
    /// </para>
    /// </summary>
    public Func<MessageEnvelope, string> RoutingKeySelector { get; set; }

    /// <summary>
    /// When <c>true</c> AND <see cref="DeclareExchange"/> is <c>false</c>, <c>InitializeAsync</c>
    /// probes the configured <see cref="ExchangeName"/> with an AMQP passive declare and retries on
    /// <c>NOT_FOUND</c> (404) until the exchange appears, the cancellation token is cancelled, or
    /// <see cref="TopologyWaitTimeout"/> (when set) elapses.
    /// <para>
    /// Use this in microservice deployments where the exchange is owned and declared by a
    /// different service. Defaults to <c>false</c> — a missing exchange surfaces the underlying
    /// <c>OperationInterruptedException</c> on the first failed AMQP operation as before.
    /// </para>
    /// <para>
    /// Only <c>NOT_FOUND</c> (404) errors trigger retry. Other channel-level errors
    /// (<c>PRECONDITION_FAILED</c>, <c>ACCESS_REFUSED</c>, etc.) and connection-level errors
    /// propagate immediately so genuine misconfiguration still fails fast.
    /// </para>
    /// </summary>
    public bool WaitForTopology { get; set; }

    /// <summary>
    /// Delay between passive-declare attempts when <see cref="WaitForTopology"/> is <c>true</c>.
    /// Defaults to 5 seconds.
    /// </summary>
    public TimeSpan TopologyWaitInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Optional ceiling on the total time the topology wait loop may consume. When <c>null</c>
    /// (default), the loop continues indefinitely until the topology appears or the
    /// <see cref="CancellationToken"/> passed to <c>InitializeAsync</c> is cancelled. Operators who
    /// want a hard deadline independent of the host's cancellation should set this explicitly.
    /// <para>
    /// The timeout is evaluated <em>after</em> each failed attempt, so the observed wait may
    /// exceed this value by up to one <see cref="TopologyWaitInterval"/>. Must be positive when set.
    /// </para>
    /// </summary>
    public TimeSpan? TopologyWaitTimeout { get; set; }

    public RabbitMqPublisherOptions()
    {
        RoutingKeySelector = envelope =>
            $"{RoutingKey}.{envelope.EntityType}.{envelope.ChangeType.ToString().ToLower()}";
    }
}
