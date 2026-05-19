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

    public RabbitMqPublisherOptions()
    {
        RoutingKeySelector = envelope =>
            $"{RoutingKey}.{envelope.EntityType}.{envelope.ChangeType.ToString().ToLower()}";
    }
}
