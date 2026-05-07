namespace RayTree.Plugins.Kafka;

public class KafkaConsumerOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string Topic { get; set; } = "entity_changes";
    public string GroupId { get; set; } = "raytree-subscriber";
    public bool FromEarliest { get; set; } = true;
    /// <summary>
    /// How long <c>Consumer.Consume()</c> blocks waiting for a message before returning
    /// an empty result.  Lower values increase CPU usage on idle topics; higher values
    /// add latency to <see cref="KafkaConsumer.Dispose"/> (which waits up to
    /// <c>2 × PollTimeoutMs + 200 ms</c> for the poll loop to exit).
    /// Default: 1000 ms — a good balance for production; tests override to a smaller value.
    /// </summary>
    public int PollTimeoutMs { get; set; } = 1000;
}
