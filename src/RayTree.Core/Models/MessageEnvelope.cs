using RayTree.Core.Tracking;

namespace RayTree.Core.Models;

/// <summary>
/// The unit passed through the message queue: entity-change metadata plus the
/// already-serialised (and optionally compressed) entity payload.
/// <br/>
/// Publishers create an envelope by serialising an <see cref="EntityChange{TEntity}"/>;
/// subscribers reconstruct a typed <see cref="EntityChange{TEntity}"/> by deserialising
/// <see cref="Payload"/> with the compressor and serialiser registered for the entity type.
/// </summary>
public class MessageEnvelope
{
    public string    EntityType    { get; set; } = string.Empty;
    public string    EntityId      { get; set; } = string.Empty;
    public ChangeType ChangeType   { get; set; }
    public Guid      CorrelationId { get; set; } = Guid.NewGuid();
    public int       Version       { get; set; } = 1;
    public DateTime  Timestamp     { get; set; } = DateTime.UtcNow;

    /// <summary>Serialised (and optionally compressed) entity state.</summary>
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    private Dictionary<string, object?>? _metadata;

    /// <summary>
    /// Consumer-private metadata bag for broker-specific state (e.g., delivery tags,
    /// lock tokens, receipt handles). Populated by the <see cref="Plugins.Consumer.IQueueConsumer"/>
    /// implementation when the envelope is yielded and consulted by that same consumer's
    /// <see cref="Plugins.Consumer.IQueueConsumer.AcknowledgeAsync"/> /
    /// <see cref="Plugins.Consumer.IQueueConsumer.NegativeAcknowledgeAsync"/> overrides.
    /// <para>
    /// Lazily allocated — incurs no cost for consumers that don't use it. NOT part of
    /// the wire format: never serialised by publishers, never inspected by handlers,
    /// and must not be relied upon outside the consumer that produced the envelope.
    /// Prefer typed extension methods over direct dictionary access at call sites.
    /// </para>
    /// </summary>
    public IDictionary<string, object?> Metadata
        => _metadata ??= new Dictionary<string, object?>(StringComparer.Ordinal);
}
