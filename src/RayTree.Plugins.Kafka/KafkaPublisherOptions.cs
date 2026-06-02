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

    /// <summary>
    /// When <c>true</c>, <c>InitializeAsync</c> waits for the configured <see cref="Topic"/> to become
    /// available on the broker before completing — instead of letting the missing topic propagate as a
    /// downstream <c>UnknownTopicOrPart</c> error from the first <c>ProduceAsync</c>.
    /// <para>
    /// Use this in microservice deployments where the topic is owned and created by a different service.
    /// Defaults to <c>false</c> — a missing topic surfaces through the underlying client exactly as today.
    /// </para>
    /// <para>
    /// The probe retries while the broker reports any of: empty <c>Topics</c> collection,
    /// per-topic <c>UnknownTopicOrPart</c>, or per-topic <c>LeaderNotAvailable</c> (a transient state during
    /// cluster bootstrap / partition-leader election). All other broker errors propagate immediately.
    /// </para>
    /// <para>
    /// <b>Auto-create caveat:</b> brokers with <c>auto.create.topics.enable=true</c> (the default on many
    /// distributions) will create the topic in response to the metadata probe itself, masking real
    /// misconfiguration (a typo in <see cref="Topic"/> still "succeeds"). Set the broker option to
    /// <c>false</c> in deployments that rely on this feature.
    /// </para>
    /// </summary>
    public bool WaitForTopic { get; set; }

    /// <summary>
    /// Delay between metadata probe attempts when <see cref="WaitForTopic"/> is <c>true</c>.
    /// Defaults to 5 seconds. Must be positive.
    /// </summary>
    public TimeSpan TopicWaitInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Optional ceiling on the total time the topic-wait loop may consume. When <c>null</c>
    /// (default), the loop continues indefinitely until the topic appears or the
    /// <see cref="CancellationToken"/> passed to <c>InitializeAsync</c> is cancelled.
    /// <para>
    /// The timeout is evaluated <em>after</em> each failed attempt, so the observed wait may
    /// exceed this value by up to one <see cref="TopicWaitInterval"/>. Must be positive when set.
    /// </para>
    /// <para>
    /// <b>Caution:</b> when this is <c>null</c> AND the tracker is constructed via the
    /// synchronous <c>ChangeTrackingBuilder.Build()</c> path (which <c>AddChangeTracking</c>
    /// uses), no cancellation token is plumbed through — a missing topic blocks startup
    /// indefinitely with no SIGTERM/Ctrl+C escape. Either set a non-null timeout, or use
    /// <c>BuildAsync(cancellationToken)</c> with the host's <c>ApplicationStopping</c> token.
    /// </para>
    /// </summary>
    public TimeSpan? TopicWaitTimeout { get; set; }

    /// <summary>
    /// Tunes the producer-rebuild policy on fatal native errors. When librdkafka surfaces an
    /// <c>Error.IsFatal == true</c> via the producer's error handler, the publisher disposes
    /// the current <c>IProducer</c> and lets the existing lazy <c>GetProducerAsync</c> path
    /// rebuild on the next <c>PublishAsync</c>. The outbox-publisher retry loop provides the
    /// outer backoff; this property controls whether RayTree participates at all
    /// (<c>Enabled = false</c> surfaces the dead producer to callers without rebuilding).
    /// </summary>
    public KafkaConnectionRecoveryOptions ConnectionRecovery { get; set; } = new();
}
