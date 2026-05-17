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
    /// </summary>
    public bool AckAfterHandler { get; set; }
}
