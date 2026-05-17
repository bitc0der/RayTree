using RayTree.Core.Models;

namespace RayTree.Core.Plugins.Consumer;

public interface IQueueConsumer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<MessageEnvelope> ConsumeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Called by <c>ChangeSubscriber</c> after all handlers for <paramref name="envelope"/>
    /// succeed. Default implementation is a no-op — at-most-once consumers already
    /// acknowledged the message at delivery time. Override this to defer the broker ACK
    /// until handler completion, providing at-least-once semantics.
    /// </summary>
    /// <param name="envelope">
    /// The envelope to acknowledge. The same instance that was yielded by
    /// <see cref="ConsumeAsync"/>; broker-specific state (e.g., delivery tag) is expected
    /// to be carried in <see cref="MessageEnvelope.Metadata"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AcknowledgeAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Called by <c>ChangeSubscriber</c> when a handler exhausts its retries and
    /// <see cref="Handling.SubscriberOptions.SkipOnFailure"/> is <c>false</c>. Default
    /// implementation is a no-op. Override to signal the broker that the message should
    /// be redelivered (RabbitMQ: NACK with requeue; Kafka: skip the commit so the offset
    /// stays at the last committed value and the message is re-read on restart).
    /// </summary>
    /// <param name="envelope">
    /// The envelope to negatively acknowledge. The same instance that was yielded by
    /// <see cref="ConsumeAsync"/>; broker-specific state is expected to be carried in
    /// <see cref="MessageEnvelope.Metadata"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task NegativeAcknowledgeAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
