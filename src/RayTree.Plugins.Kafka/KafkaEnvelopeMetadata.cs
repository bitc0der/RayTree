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

    /// <summary>
    /// Reads and <b>removes</b> the consume-result metadata in one atomic-style step so
    /// that a subsequent Ack/Nack call cannot accidentally commit/seek the same offset
    /// twice (defensive against double-dispatch by callers).
    /// </summary>
    internal static bool TryTakeConsumeResult(
        this MessageEnvelope envelope,
        out ConsumeResult<string, byte[]>? result)
    {
        if (envelope.Metadata.TryGetValue(ConsumeResultKey, out var raw)
            && raw is ConsumeResult<string, byte[]> r)
        {
            envelope.Metadata.Remove(ConsumeResultKey);
            result = r;
            return true;
        }
        result = null;
        return false;
    }
}
