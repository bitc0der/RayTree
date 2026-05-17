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
}
