using RayTree.Core.Handling;
using RayTree.Core.Plugins.Consumer;

namespace RayTree.Core.Tracking;

/// <summary>
/// Post-fork builder returned by <see cref="IEntityBuilder{TEntity}.UseConsumerFactory"/>.
/// Registers <em>named</em> change handlers that each receive their own broker subscription,
/// retry budget, and deduplication namespace in <em>Isolated</em> dispatch mode.
///
/// <para><strong>Handler names</strong> are stable identifiers that map directly to broker
/// topology (Kafka consumer-group ID, RabbitMQ queue name, etc.) via the
/// <see cref="Func{T,TResult}"/> factory supplied to
/// <see cref="IEntityBuilder{TEntity}.UseConsumerFactory"/>. The factory is invoked exactly
/// once per distinct name at <c>Build()</c> time. Renaming a handler at deployment time is
/// equivalent to creating a new broker subscription — the old name's offsets and messages
/// remain; the new name starts fresh. Treat handler names as part of the service's
/// public deployment contract.</para>
///
/// <para><strong>Accumulation:</strong> each call adds a handler. The pair
/// <c>(action, handlerName)</c> must be unique within the entity; duplicates cause
/// <see cref="InvalidOperationException"/> at <c>Build()</c> time. Handlers registered
/// under the same name but different actions share one <see cref="IQueueConsumer"/> and
/// one consume loop; the loop selects the matching registration by <c>ChangeType</c>
/// on inbound messages.</para>
///
/// <para><strong>Per-handler options:</strong> pass <see cref="SubscriberOptions"/> inline
/// on any registration for a given handler name. The first non-null options encountered for
/// a name are applied to that handler's consume loop. Subsequent registrations under the same
/// name may omit options — they inherit the options already associated with the name.</para>
///
/// <para><strong>Dedup key:</strong> <c>$"{correlationId}:{handlerName}"</c>. Each named
/// handler has an independent deduplication namespace, so a failed redelivery under one
/// handler does not force other handlers to re-execute.</para>
///
/// <para><strong>Validation at registration time:</strong> passing a null or empty
/// <paramref name="handlerName"/> throws <see cref="ArgumentException"/> immediately.
/// Factory returning <c>null</c> or the same <see cref="IQueueConsumer"/> instance for
/// two distinct names throws <see cref="InvalidOperationException"/> at <c>Build()</c>.</para>
/// </summary>
public interface IIsolatedHandlerBuilder<TEntity> where TEntity : class
{
    /// <summary>
    /// Adds a named handler invoked only on <see cref="ChangeType.Insert"/> events.
    /// </summary>
    /// <param name="handlerName">
    /// A non-null, non-empty stable identifier for this handler. Used as the
    /// dedup-key suffix and passed to the consumer factory.
    /// </param>
    /// <param name="handler">The handler delegate to invoke.</param>
    /// <param name="options">
    /// Optional per-handler <see cref="SubscriberOptions"/>. The first non-null options
    /// supplied for a given handler name apply to that handler's consume loop.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown immediately if <paramref name="handlerName"/> is null or empty.
    /// </exception>
    IIsolatedHandlerBuilder<TEntity> OnInsert(string handlerName, ChangeHandlerAsync<TEntity> handler,
        SubscriberOptions? options = null);

    /// <summary>
    /// Adds a named handler invoked only on <see cref="ChangeType.Update"/> events.
    /// </summary>
    /// <param name="handlerName">
    /// A non-null, non-empty stable identifier for this handler.
    /// </param>
    /// <param name="handler">The handler delegate to invoke.</param>
    /// <param name="options">
    /// Optional per-handler <see cref="SubscriberOptions"/>. The first non-null options
    /// supplied for a given handler name apply to that handler's consume loop.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown immediately if <paramref name="handlerName"/> is null or empty.
    /// </exception>
    IIsolatedHandlerBuilder<TEntity> OnUpdate(string handlerName, ChangeHandlerAsync<TEntity> handler,
        SubscriberOptions? options = null);

    /// <summary>
    /// Adds a named handler invoked only on <see cref="ChangeType.Delete"/> events.
    /// </summary>
    /// <param name="handlerName">
    /// A non-null, non-empty stable identifier for this handler.
    /// </param>
    /// <param name="handler">The handler delegate to invoke.</param>
    /// <param name="options">
    /// Optional per-handler <see cref="SubscriberOptions"/>. The first non-null options
    /// supplied for a given handler name apply to that handler's consume loop.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown immediately if <paramref name="handlerName"/> is null or empty.
    /// </exception>
    IIsolatedHandlerBuilder<TEntity> OnDelete(string handlerName, ChangeHandlerAsync<TEntity> handler,
        SubscriberOptions? options = null);

    /// <summary>
    /// Adds a named handler for the specified change type, or for all change types when
    /// <paramref name="changeType"/> is <c>null</c>.
    /// </summary>
    /// <param name="handlerName">
    /// A non-null, non-empty stable identifier for this handler.
    /// </param>
    /// <param name="changeType">
    /// The change type to filter on, or <c>null</c> to match all change types.
    /// </param>
    /// <param name="handler">The handler delegate to invoke.</param>
    /// <param name="options">
    /// Optional per-handler <see cref="SubscriberOptions"/>. The first non-null options
    /// supplied for a given handler name apply to that handler's consume loop.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown immediately if <paramref name="handlerName"/> is null or empty.
    /// </exception>
    IIsolatedHandlerBuilder<TEntity> OnChange(string handlerName, ChangeType? changeType,
        ChangeHandlerAsync<TEntity> handler, SubscriberOptions? options = null);
}
