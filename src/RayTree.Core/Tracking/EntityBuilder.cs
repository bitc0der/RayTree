using RayTree.Core.Tracking;

namespace RayTree.Plugins;

internal class EntityBuilder(ChangeTrackingBuilder parent, Type entityType)
    : IEntityBuilder
{
    public IEntityBuilder UseRepository(IRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);

        parent.AddRepositoryOverride(entityType, repository);
        return this;
    }

    public IEntityBuilder UseOutbox(IOutbox outbox)
    {
        ArgumentNullException.ThrowIfNull(outbox);

        parent.AddOutboxOverride(entityType, outbox);
        return this;
    }

    public IEntityBuilder UseQueue(IQueuePublisher queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        parent.AddQueueOverride(entityType, queue);
        return this;
    }

    public IEntityBuilder UseSerializer(IChangeSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);

        parent.AddSerializerOverride(entityType, serializer);
        return this;
    }

    public IEntityBuilder UseCompressor(IChangeCompressor compressor)
    {
        ArgumentNullException.ThrowIfNull(compressor);

        parent.AddCompressorOverride(entityType, compressor);
        return this;
    }
}
