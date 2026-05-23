using RayTree.Core.Plugins.Compression;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Repository;
using RayTree.Core.Plugins.Serialization;

namespace RayTree.Core.Distribution;

/// <summary>
/// Fluent builder scoped to a single entity type on the publisher side.
/// </summary>
public interface IEntityPublisherBuilder<TEntity> where TEntity : class
{
    IEntityPublisherBuilder<TEntity> UseOutbox(IOutbox outbox);
    IEntityPublisherBuilder<TEntity> UsePublisher(IQueuePublisher queue);
    IEntityPublisherBuilder<TEntity> UseSerializer(IChangeSerializer serializer);
    IEntityPublisherBuilder<TEntity> UseCompressor(IChangeCompressor compressor);
    IEntityPublisherBuilder<TEntity> UseRepository(IRepository repository);
}
