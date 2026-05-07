using RayTree.Core.Handling;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Tracking;

namespace RayTree.Subscriber;

/// <summary>
/// Fluent builder scoped to a single entity type on the subscriber side.
/// Every method returns <c>this</c> for chaining, and the entity type parameter appears
/// only once (in <see cref="IChangeSubscriberBuilder.ForEntity{TEntity}"/>), so there is
/// no repetition across the per-entity configuration block.
/// </summary>
public interface IEntitySubscriberBuilder<TEntity> where TEntity : class
{
    /// <summary>Sets the queue consumer for this entity type.</summary>
    IEntitySubscriberBuilder<TEntity> UseQueue(IQueueConsumer consumer);

    /// <summary>Overrides the global serializer for this entity.</summary>
    IEntitySubscriberBuilder<TEntity> UseSerializer(IChangeSerializer serializer);

    /// <summary>Overrides the global compressor for this entity.</summary>
    IEntitySubscriberBuilder<TEntity> UseCompressor(IChangeCompressor compressor);

    /// <summary>
    /// Overrides the global retry / skip-on-failure options for this entity only.
    /// The callback receives a copy of the global options so you can change individual
    /// properties without repeating unchanged values.
    /// </summary>
    IEntitySubscriberBuilder<TEntity> UseOptions(Action<SubscriberOptions> configure);

    /// <summary>Registers a handler invoked only on <see cref="ChangeType.Insert"/> events.</summary>
    IEntitySubscriberBuilder<TEntity> OnInsert(ChangeHandlerAsync<TEntity> handler);

    /// <summary>Registers a handler invoked only on <see cref="ChangeType.Update"/> events.</summary>
    IEntitySubscriberBuilder<TEntity> OnUpdate(ChangeHandlerAsync<TEntity> handler);

    /// <summary>Registers a handler invoked only on <see cref="ChangeType.Delete"/> events.</summary>
    IEntitySubscriberBuilder<TEntity> OnDelete(ChangeHandlerAsync<TEntity> handler);

    /// <summary>
    /// Registers a handler for the specified change type, or for all types when
    /// <paramref name="changeType"/> is <c>null</c>.
    /// </summary>
    IEntitySubscriberBuilder<TEntity> OnChange(ChangeType? changeType, ChangeHandlerAsync<TEntity> handler);
}
