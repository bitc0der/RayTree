using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Repository;
using RayTree.Core.Plugins.Serialization;

namespace RayTree.Core.Distribution;

/// <summary>
/// Fluent builder that produces a <see cref="ChangePublisher"/> with global defaults
/// and optional per-entity overrides. Parallel to <see cref="RayTree.Core.Handling.IChangeSubscriberBuilder"/>
/// on the subscriber side.
/// </summary>
public interface IChangePublisherBuilder
{
    IChangePublisherBuilder UseOutbox<T>(Func<Type, IOutbox> factory) where T : IOutbox;
    IChangePublisherBuilder UseQueue<T>(Func<Type, IQueuePublisher> factory) where T : IQueuePublisher;
    IChangePublisherBuilder UseSerializer<T>(Func<Type, IChangeSerializer> factory) where T : IChangeSerializer;
    IChangePublisherBuilder UseCompressor<T>(Func<Type, IChangeCompressor> factory) where T : IChangeCompressor;
    IChangePublisherBuilder UseRepository<T>(Func<Type, IRepository> factory) where T : IRepository;
    IChangePublisherBuilder UseOptions(Action<OutboxPublisherOptions> configure);

    /// <summary>
    /// Configures a single entity type via a scoped callback. Returns the parent builder
    /// so calls can be chained.
    /// </summary>
    IChangePublisherBuilder ForEntity<TEntity>(Action<IEntityPublisherBuilder<TEntity>> configure)
        where TEntity : class;

    ChangePublisher Build();
}
