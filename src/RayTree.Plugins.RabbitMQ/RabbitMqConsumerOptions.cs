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
}
