using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Tracking;

namespace RayTree.Core.Handling;

/// <summary>
/// Fluent builder scoped to a single entity type on the subscriber side.
/// Every method returns <c>this</c> for chaining, and the entity type parameter appears
/// only once (in <see cref="IChangeSubscriberBuilder.ForEntity{TEntity}"/>), so there is
/// no repetition across the per-entity configuration block.
/// </summary>
public interface IEntitySubscriberBuilder<TEntity> where TEntity : class
{
    /// <summary>Sets the queue consumer for this entity type.</summary>
    IEntitySubscriberBuilder<TEntity> UseConsumer(IQueueConsumer consumer);

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
    /// Registers a handler for the specified change type. Every handler must bind to a
    /// concrete <see cref="ChangeType"/>; the previous wildcard <c>null</c> form was
    /// removed. To react to multiple change types with the same logic, call this method
    /// (or <see cref="OnInsert"/>/<see cref="OnUpdate"/>/<see cref="OnDelete"/>) once per type.
    /// </summary>
    IEntitySubscriberBuilder<TEntity> OnChange(ChangeType changeType, ChangeHandlerAsync<TEntity> handler);
}
