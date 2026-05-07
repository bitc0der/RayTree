namespace RayTree.Plugins.RabbitMQ;

public class RabbitMqConsumerOptions
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string QueueName { get; set; } = "entity_changes";
    public bool DeclareQueue { get; set; } = true;
    public bool Durable { get; set; } = true;
    public ushort PrefetchCount { get; set; } = 10;

    /// <summary>
    /// When set, the queue is bound to this exchange during initialization.
    /// Required when the publisher writes to a named exchange rather than the default exchange.
    /// </summary>
    public string? ExchangeName { get; set; }

    /// <summary>Routing key pattern used when binding the queue to the exchange. Defaults to "#" (match all).</summary>
    public string BindingKey { get; set; } = "#";
}
