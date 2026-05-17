using RayTree.Core.Handling;

namespace RayTree.Core.Tracking;

/// <summary>
/// Post-fork builder returned by <see cref="IEntityBuilder{TEntity}.UseConsumer"/>.
/// Registers anonymous (un-named) change handlers that share a single broker delivery in
/// <em>Shared</em> dispatch mode.
///
/// <para><strong>Accumulation:</strong> each call adds a handler to the entity's handler list;
/// later calls do not replace earlier ones. Handlers execute sequentially in registration order
/// on every matching delivery.</para>
///
/// <para><strong>Dedup key:</strong> <c>correlationId</c> (message-scoped). If a handler
/// exhausts retries with <c>SkipOnFailure = false</c>, the dedup mark is reverted so a
/// redelivered message will re-invoke every handler, including those that previously
/// succeeded. Handlers must therefore be idempotent.</para>
///
/// <para><strong>Note:</strong> RabbitMQ and Kafka consumers ACK/commit before the subscriber
/// processes the message, so broker-driven redelivery does not fire for Shared-mode entities.
/// For true broker-driven at-least-once retry per handler, use
/// <see cref="IEntityBuilder{TEntity}.UseConsumerFactory"/> to switch to Isolated mode.</para>
/// </summary>
public interface ISharedHandlerBuilder<TEntity> where TEntity : class
{
    /// <summary>
    /// Adds a handler invoked only on <see cref="ChangeType.Insert"/> events.
    /// Accumulates with any previously registered handlers for the same entity.
    /// </summary>
    ISharedHandlerBuilder<TEntity> OnInsert(ChangeHandlerAsync<TEntity> handler);

    /// <summary>
    /// Adds a handler invoked only on <see cref="ChangeType.Update"/> events.
    /// Accumulates with any previously registered handlers for the same entity.
    /// </summary>
    ISharedHandlerBuilder<TEntity> OnUpdate(ChangeHandlerAsync<TEntity> handler);

    /// <summary>
    /// Adds a handler invoked only on <see cref="ChangeType.Delete"/> events.
    /// Accumulates with any previously registered handlers for the same entity.
    /// </summary>
    ISharedHandlerBuilder<TEntity> OnDelete(ChangeHandlerAsync<TEntity> handler);

    /// <summary>
    /// Adds a handler for the specified change type. Accumulates with any previously
    /// registered handlers for the same entity. To react to multiple change types with
    /// the same logic, call this method (or the type-specific overloads) once per type;
    /// there is no longer a wildcard <c>null</c> form — every handler must bind to a
    /// concrete <see cref="ChangeType"/>.
    /// </summary>
    ISharedHandlerBuilder<TEntity> OnChange(ChangeType changeType, ChangeHandlerAsync<TEntity> handler);
}
