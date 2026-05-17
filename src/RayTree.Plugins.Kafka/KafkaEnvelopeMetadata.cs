using Confluent.Kafka;
using RayTree.Core.Models;

namespace RayTree.Plugins.Kafka;

/// <summary>
/// Typed accessors for Kafka-specific state stashed in
/// <see cref="MessageEnvelope.Metadata"/>. Used by <see cref="KafkaConsumer"/> to
/// correlate an envelope back to the original <see cref="ConsumeResult{TKey,TValue}"/>
/// when the offset commit is deferred until after handler completion.
/// </summary>
internal static class KafkaEnvelopeMetadata
{
    internal const string ConsumeResultKey = "raytree.kafka.consume_result";

    internal static void SetConsumeResult(this MessageEnvelope envelope, ConsumeResult<string, byte[]> result)
        => envelope.Metadata[ConsumeResultKey] = result;

    internal static bool TryGetConsumeResult(
        this MessageEnvelope envelope,
        out ConsumeResult<string, byte[]>? result)
    {
        if (envelope.Metadata.TryGetValue(ConsumeResultKey, out var raw)
            && raw is ConsumeResult<string, byte[]> r)
        {
            result = r;
            return true;
        }
        result = null;
        return false;
    }
}
