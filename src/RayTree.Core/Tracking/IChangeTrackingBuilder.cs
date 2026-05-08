using RayTree.Core.Distribution;
using RayTree.Core.Handling;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Deduplication;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Serialization;

namespace RayTree.Core.Tracking;

public interface IChangeTrackingBuilder
{
    // Publisher global configuration
    IChangeTrackingBuilder UseOutbox<T>(Func<Type, IOutbox> factory) where T : IOutbox;
    IChangeTrackingBuilder UseQueue<T>(Func<Type, IQueuePublisher> factory) where T : IQueuePublisher;
    IChangeTrackingBuilder UseSerializer<T>(Func<Type, IChangeSerializer> factory) where T : IChangeSerializer;
    IChangeTrackingBuilder UseCompressor<T>(Func<Type, IChangeCompressor> factory) where T : IChangeCompressor;
    IChangeTrackingBuilder UsePublisherOptions(Action<OutboxPublisherOptions> configure);

    // Subscriber global configuration
    IChangeTrackingBuilder UseSubscriberOptions(Action<SubscriberOptions> configure);
    IChangeTrackingBuilder UseDeduplicationStore(IDeduplicationStore store);

    /// <summary>
    /// Configures a single entity type using a scoped callback. Global serializer/compressor/publisher
    /// options apply to all entities that do not provide an explicit per-entity override inside
    /// the callback. Returns the parent builder so calls can be chained.
    /// </summary>
    IChangeTrackingBuilder ForEntity<TEntity>(Action<IEntityBuilder<TEntity>> configure) where TEntity : class;

    EntityChangeTracker Build();
    Task<EntityChangeTracker> BuildAsync(CancellationToken cancellationToken = default);
}
