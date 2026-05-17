using RayTree.Core.Models;

namespace RayTree.Plugins.RabbitMQ;

/// <summary>
/// Typed accessors for RabbitMQ-specific state stashed in
/// <see cref="MessageEnvelope.Metadata"/>. Keeps the stringly-typed dictionary key
/// in one place and provides a discoverable API surface.
/// </summary>
internal static class RabbitMqEnvelopeMetadata
{
    internal const string DeliveryTagKey = "raytree.rmq.delivery_tag";

    internal static void SetDeliveryTag(this MessageEnvelope envelope, ulong tag)
        => envelope.Metadata[DeliveryTagKey] = tag;

    /// <summary>
    /// Reads and <b>removes</b> the delivery-tag metadata in one step so a subsequent
    /// Ack/Nack call cannot accidentally double-acknowledge the same delivery (which
    /// RabbitMQ rejects with <c>PRECONDITION_FAILED — unknown delivery tag</c>).
    /// </summary>
    internal static bool TryTakeDeliveryTag(this MessageEnvelope envelope, out ulong tag)
    {
        if (envelope.Metadata.TryGetValue(DeliveryTagKey, out var raw) && raw is ulong t)
        {
            envelope.Metadata.Remove(DeliveryTagKey);
            tag = t;
            return true;
        }
        tag = 0;
        return false;
    }
}
