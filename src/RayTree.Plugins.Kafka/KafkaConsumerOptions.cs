namespace RayTree.Plugins.Kafka;

public class KafkaConsumerOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string Topic { get; set; } = "entity_changes";
    public string GroupId { get; set; } = "raytree-subscriber";
    public bool FromEarliest { get; set; } = true;
    public int PollTimeoutMs { get; set; } = 100;
}
