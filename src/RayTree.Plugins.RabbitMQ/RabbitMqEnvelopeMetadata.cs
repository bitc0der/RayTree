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

    internal static bool TryGetDeliveryTag(this MessageEnvelope envelope, out ulong tag)
    {
        if (envelope.Metadata.TryGetValue(DeliveryTagKey, out var raw) && raw is ulong t)
        {
            tag = t;
            return true;
        }
        tag = 0;
        return false;
    }
}
