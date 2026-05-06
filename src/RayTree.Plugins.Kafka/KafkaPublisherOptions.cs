namespace RayTree.Plugins.Kafka;

public class KafkaPublisherOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string Topic { get; set; } = "entity_changes";
    public string? Acks { get; set; }
    public int? MessageMaxBytes { get; set; }
}
