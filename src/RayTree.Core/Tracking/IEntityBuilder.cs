using RayTree.Core.Handling;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Repository;
using RayTree.Core.Plugins.Serialization;

namespace RayTree.Core.Tracking;

/// <summary>
/// Per-entity configuration entry point for both publisher and subscriber sides.
///
/// <para><strong>Mode selection:</strong> call one of the two consumer-binding methods to
/// select a handler-dispatch mode and fork the fluent chain into the appropriate builder:
/// <list type="bullet">
///   <item>
///     <see cref="UseConsumer"/> — <em>Shared</em> mode. Returns
///     <see cref="ISharedHandlerBuilder{TEntity}"/> whose handler methods take only a
///     delegate (no name). All handlers share one broker delivery; dispatch is sequential
///     in registration order.
///   </item>
///   <item>
///     <see cref="UseConsumerFactory"/> — <em>Isolated</em> mode. Returns
///     <see cref="IIsolatedHandlerBuilder{TEntity}"/> whose handler methods require a
///     non-null, non-empty <c>handlerName</c>. Each named handler has its own broker
///     subscription, retry budget, and dedup namespace (key:
///     <c>$"{correlationId}:{handlerName}"</c>).
///   </item>
/// </list>
/// </para>
///
/// <para>Handler-registration methods (<c>OnInsert</c>, <c>OnUpdate</c>, <c>OnDelete</c>,
/// <c>OnChange</c>) are <em>only</em> available on the post-fork builders — they do not
/// exist on this interface, so the compiler prevents registering handlers before binding
/// a consumer.</para>
///
/// <para>Subscriber options (<see cref="UseSubscriberOptions"/>) may be set before or after
/// the consumer-binding call; they apply to both modes.</para>
/// </summary>
public interface IEntityBuilder<TEntity> where TEntity : class
{
    // -------------------------------------------------------------------------
    // Publisher side
    // -------------------------------------------------------------------------

    /// <summary>Overrides the global repository for this entity type.</summary>
    IEntityBuilder<TEntity> UseRepository(IRepository repository);

    /// <summary>Overrides the global outbox for this entity type.</summary>
    IEntityBuilder<TEntity> UseOutbox(IOutbox outbox);

    /// <summary>Overrides the global queue publisher for this entity type.</summary>
    IEntityBuilder<TEntity> UsePublisher(IQueuePublisher queue);

    /// <summary>Overrides the global serializer for this entity type.</summary>
    IEntityBuilder<TEntity> UseSerializer(IChangeSerializer serializer);

    /// <summary>Overrides the global compressor for this entity type.</summary>
    IEntityBuilder<TEntity> UseCompressor(IChangeCompressor compressor);

    // -------------------------------------------------------------------------
    // Subscriber side — pre-fork
    // -------------------------------------------------------------------------

    /// <summary>
    /// Overrides the global subscriber options (retry count, retry delay, skip-on-failure,
    /// dedup retention, etc.) for this entity only.
    /// </summary>
    IEntityBuilder<TEntity> UseSubscriberOptions(Action<SubscriberOptions> configure);

    // -------------------------------------------------------------------------
    // Subscriber side — consumer binding (forks the builder chain)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Selects <em>Shared</em> handler-dispatch mode and returns
    /// <see cref="ISharedHandlerBuilder{TEntity}"/> for registering anonymous handlers.
    ///
    /// <para>All handlers registered on the returned builder share a single delivery of each
    /// message. They execute sequentially in registration order. The deduplication key is
    /// <c>correlationId</c> (message-scoped).</para>
    /// </summary>
    /// <param name="consumer">The queue consumer that delivers messages to this entity.</param>
    ISharedHandlerBuilder<TEntity> UseConsumer(IQueueConsumer consumer);

    /// <summary>
    /// Selects <em>Isolated</em> handler-dispatch mode and returns
    /// <see cref="IIsolatedHandlerBuilder{TEntity}"/> for registering named handlers.
    ///
    /// <para>The factory is invoked exactly once per distinct handler name at
    /// <c>Build()</c> time. Each named handler receives its own consumer instance,
    /// retry budget, and deduplication namespace (key:
    /// <c>$"{correlationId}:{handlerName}"</c>).</para>
    ///
    /// <para>The factory should encode handler identity into broker-level configuration —
    /// for example, <c>options with { GroupId = $"orders-{handlerName}" }</c> for Kafka.
    /// The factory must return a distinct, non-null <see cref="IQueueConsumer"/> instance
    /// per name; returning <c>null</c> or the same instance for two names throws
    /// <see cref="InvalidOperationException"/> at <c>Build()</c>.</para>
    /// </summary>
    /// <param name="factory">
    /// A delegate that, given a handler name, returns the <see cref="IQueueConsumer"/>
    /// dedicated to that handler.
    /// </param>
    IIsolatedHandlerBuilder<TEntity> UseConsumerFactory(Func<string, IQueueConsumer> factory);
}
