using RayTree.Core.Models;

namespace RayTree.Plugins.Kafka;

public class KafkaPublisherOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string Topic { get; set; } = "entity_changes";
    public string? Acks { get; set; }
    public int? MessageMaxBytes { get; set; }

    /// <summary>
    /// Selects the Kafka partition key for each outgoing message.
    /// Messages with the same key are guaranteed to land on the same partition,
    /// preserving per-key ordering.
    /// <para>
    /// Defaults to <c>"{EntityType}:{EntityId}"</c>, which keeps all changes for
    /// a given entity on one partition. Override to shard by a different field —
    /// for example by tenant, aggregate root, or any value extracted from the envelope.
    /// </para>
    /// </summary>
    public Func<MessageEnvelope, string> KeySelector { get; set; } =
        static envelope => $"{envelope.EntityType}:{envelope.EntityId}";
}
