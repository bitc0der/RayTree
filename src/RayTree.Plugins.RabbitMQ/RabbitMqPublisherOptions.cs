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
