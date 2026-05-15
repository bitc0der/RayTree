using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Repository;
using RayTree.Core.Plugins.Serialization;

namespace RayTree.Core.Distribution;

internal sealed class EntityPublisherBuilder<TEntity>(ChangePublisherBuilder parent)
    : IEntityPublisherBuilder<TEntity>
    where TEntity : class
{
    private readonly Type _entityType = typeof(TEntity);

    public IEntityPublisherBuilder<TEntity> UseOutbox(IOutbox outbox)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        parent.AddOutboxOverride(_entityType, outbox);
        return this;
    }

    public IEntityPublisherBuilder<TEntity> UsePublisher(IQueuePublisher queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        parent.AddQueueOverride(_entityType, queue);
        return this;
    }

    public IEntityPublisherBuilder<TEntity> UseSerializer(IChangeSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        parent.AddSerializerOverride(_entityType, serializer);
        return this;
    }

    public IEntityPublisherBuilder<TEntity> UseCompressor(IChangeCompressor compressor)
    {
        ArgumentNullException.ThrowIfNull(compressor);
        parent.AddCompressorOverride(_entityType, compressor);
        return this;
    }

    public IEntityPublisherBuilder<TEntity> UseRepository(IRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        parent.AddRepositoryOverride(_entityType, repository);
        return this;
    }
}
