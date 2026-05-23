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

    /// <summary>
    /// Controls when the Kafka offset commit happens.
    /// <list type="bullet">
    ///   <item><c>false</c> (default — at-most-once): the offset is committed on the poll
    ///   thread immediately after parsing the message, before the envelope is even handed
    ///   to <c>ChangeSubscriber</c>. A crash between commit and handler completion loses
    ///   the message — it will be skipped on restart because the offset has advanced.</item>
    ///   <item><c>true</c> (at-least-once): the offset commit is deferred until
    ///   <c>ChangeSubscriber</c> confirms the handler succeeded. A crash leaves the offset
    ///   at the previous value so Kafka redelivers from there on the next read. Combined
    ///   with the subscriber's deduplication store this yields effectively-once semantics.</item>
    /// </list>
    /// <para>
    /// When <c>true</c>, set <see cref="RayTree.Core.Handling.SubscriberOptions.MaxDegreeOfParallelism"/>
    /// to <c>1</c> per partition. Kafka offset commits are monotonic — concurrent commits
    /// of out-of-order offsets could advance the committed offset past an in-flight message,
    /// undoing the at-least-once guarantee.
    /// </para>
    /// <para>
    /// <b>Commit latency:</b> deferred commits are applied on the poll thread between
    /// <c>Consume()</c> calls. On a busy partition this happens almost immediately
    /// (next message arrival); on an idle partition the commit waits up to
    /// <see cref="PollTimeoutMs"/> ms for the next poll cycle. Lower <c>PollTimeoutMs</c>
    /// if commit responsiveness on idle topics matters for your workload (trade: CPU).
    /// </para>
    /// <para>
    /// <b>Handler failure (NACK):</b> when a handler exhausts retries with
    /// <see cref="RayTree.Core.Handling.SubscriberOptions.SkipOnFailure"/> = <c>false</c>,
    /// the consumer performs an in-process <c>Seek</c> back to the failed message's offset
    /// so it (and everything after) is redelivered on the next poll — without requiring
    /// a process restart or partition reassignment.
    /// </para>
    /// </summary>
    public bool AckAfterHandler { get; set; }

    /// <summary>
    /// When <c>true</c>, <c>InitializeAsync</c> waits for the configured <see cref="Topic"/> to become
    /// available on the broker before building the underlying consumer or calling <c>Subscribe</c>.
    /// Without this, a missing topic causes <c>Consume</c> to return null/empty results indefinitely
    /// while librdkafka logs <c>UnknownTopicOrPart</c> warnings internally.
    /// <para>
    /// Use this in microservice deployments where the topic is owned and created by a different service.
    /// Defaults to <c>false</c>.
    /// </para>
    /// <para>
    /// The probe retries while the broker reports any of: empty <c>Topics</c> collection,
    /// per-topic <c>UnknownTopicOrPart</c>, or per-topic <c>LeaderNotAvailable</c>. All other broker
    /// errors propagate immediately.
    /// </para>
    /// <para>
    /// <b>Auto-create caveat:</b> brokers with <c>auto.create.topics.enable=true</c> will create the
    /// topic in response to the metadata probe itself, masking real misconfiguration.
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
    /// <see cref="CancellationToken"/> passed to <c>InitializeAsync</c> is cancelled. Must be
    /// positive when set.
    /// <para>
    /// <b>Caution:</b> when this is <c>null</c> AND the tracker is constructed via the
    /// synchronous <c>ChangeTrackingBuilder.Build()</c> path (which <c>AddChangeTracking</c>
    /// uses), no cancellation token is plumbed through — a missing topic blocks startup
    /// indefinitely with no SIGTERM/Ctrl+C escape. Either set a non-null timeout, or use
    /// <c>BuildAsync(cancellationToken)</c> with the host's <c>ApplicationStopping</c> token.
    /// </para>
    /// </summary>
    public TimeSpan? TopicWaitTimeout { get; set; }
}
