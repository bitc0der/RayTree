using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Serialization;
using RayTree.Subscriber;
using RayTree.Subscriber.Plugins.Deduplication;

namespace RayTree.Core.Handling;

/// <summary>
/// Fluent builder for the subscriber side.  Global settings — serializer, compressor, retry
/// options, and deduplication store — apply to every entity that does not supply an explicit
/// per-entity override inside <see cref="ForEntity{TEntity}"/>.
/// </summary>
public interface IChangeSubscriberBuilder
{
    /// <summary>Sets the global serializer used by all entities that don't override it.</summary>
    IChangeSubscriberBuilder UseSerializer(IChangeSerializer serializer);

    /// <summary>Sets the global compressor used by all entities that don't override it.</summary>
    IChangeSubscriberBuilder UseCompressor(IChangeCompressor compressor);

    /// <summary>Configures global retry and skip-on-failure behaviour.</summary>
    IChangeSubscriberBuilder UseOptions(Action<SubscriberOptions> configure);

    /// <summary>Registers a custom deduplication store shared by all entity consumers.</summary>
    IChangeSubscriberBuilder UseDeduplicationStore(IDeduplicationStore store);

    /// <summary>
    /// Configures a single entity type via a scoped callback.  The callback receives an
    /// <see cref="IEntitySubscriberBuilder{TEntity}"/> that inherits all global defaults and
    /// can override them per entity.  Returns the parent builder so calls can be chained.
    /// </summary>
    IChangeSubscriberBuilder ForEntity<TEntity>(Action<IEntitySubscriberBuilder<TEntity>> configure)
        where TEntity : class;

    /// <summary>Builds and returns the configured <see cref="ChangeSubscriber"/>.</summary>
    ChangeSubscriber Build();
}
