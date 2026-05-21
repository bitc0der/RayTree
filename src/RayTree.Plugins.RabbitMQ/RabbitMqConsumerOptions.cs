namespace RayTree.Plugins.RabbitMQ;

public class RabbitMqConsumerOptions
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string QueueName { get; set; } = "entity_changes";
    public bool DeclareQueue { get; set; } = true;
    public bool Durable { get; set; } = true;
    public ushort PrefetchCount { get; set; } = 10;

    /// <summary>
    /// When set, the queue is bound to this exchange during initialization.
    /// Required when the publisher writes to a named exchange rather than the default exchange.
    /// </summary>
    public string? ExchangeName { get; set; }

    /// <summary>Routing key pattern used when binding the queue to the exchange. Defaults to "#" (match all).</summary>
    public string BindingKey { get; set; } = "#";

    /// <summary>
    /// Controls when the RabbitMQ <c>basic.ack</c> is sent to the broker.
    /// <list type="bullet">
    ///   <item><c>false</c> (default — at-most-once): ACK fires inside the broker
    ///   delivery callback, immediately after the envelope is buffered in process memory.
    ///   A process crash after this point loses the message — the broker considers it
    ///   delivered. Lowest latency, no redelivery guarantee.</item>
    ///   <item><c>true</c> (at-least-once): ACK is deferred until <c>ChangeSubscriber</c>
    ///   confirms all handlers completed successfully. A crash mid-processing leaves the
    ///   message unacknowledged so RabbitMQ requeues it on the next connection. Combined
    ///   with the subscriber's deduplication store this yields effectively-once semantics
    ///   under normal operation.</item>
    /// </list>
    /// When <c>true</c>, set <see cref="RayTree.Core.Handling.SubscriberOptions.MaxDegreeOfParallelism"/>
    /// per your throughput / ordering requirements; ACKs are correlated to envelopes via
    /// <see cref="RayTree.Core.Models.MessageEnvelope.Metadata"/> so any concurrency level is safe.
    /// </summary>
    public bool AckAfterHandler { get; set; }

    /// <summary>
    /// When <c>true</c>, <c>InitializeAsync</c> waits for externally-owned RabbitMQ topology to
    /// appear instead of failing on <c>NOT_FOUND</c>. Specifically:
    /// <list type="bullet">
    ///   <item>If <see cref="DeclareQueue"/> is <c>false</c>, the consumer probes
    ///   <see cref="QueueName"/> with a passive declare and retries on <c>NOT_FOUND</c>.</item>
    ///   <item>If <see cref="ExchangeName"/> is non-empty, the consumer probes the exchange
    ///   with a passive declare before <c>QueueBind</c> and retries on <c>NOT_FOUND</c>.</item>
    /// </list>
    /// Defaults to <c>false</c> — missing topology surfaces the underlying
    /// <c>OperationInterruptedException</c> on the first failed AMQP operation as before.
    /// <para>
    /// Only <c>NOT_FOUND</c> (404) errors trigger retry. Other channel- and connection-level
    /// errors propagate immediately so genuine misconfiguration still fails fast.
    /// </para>
    /// </summary>
    public bool WaitForTopology { get; set; }

    /// <summary>
    /// Delay between passive-declare attempts when <see cref="WaitForTopology"/> is <c>true</c>.
    /// Defaults to 5 seconds.
    /// </summary>
    public TimeSpan TopologyWaitInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Optional ceiling on the total time the topology wait loop may consume. When <c>null</c>
    /// (default), the loop continues indefinitely until the topology appears or the
    /// <see cref="CancellationToken"/> passed to <c>InitializeAsync</c> is cancelled.
    /// </summary>
    public TimeSpan? TopologyWaitTimeout { get; set; }
}
