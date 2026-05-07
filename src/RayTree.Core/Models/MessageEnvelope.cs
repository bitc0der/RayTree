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
}
