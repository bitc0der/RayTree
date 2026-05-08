using RayTree.Core.Distribution;
using RayTree.Core.Handling;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Repository;
using RayTree.Core.Plugins.Serialization;

namespace RayTree.Core.Tracking;

internal sealed class EntityBuilder<TEntity>(ChangePublisherBuilder publisherBuilder, ChangeSubscriberBuilder subscriberBuilder)
    : IEntityBuilder<TEntity>
    where TEntity : class
{
    private readonly EntityPublisherBuilder<TEntity> _pubBuilder = new(publisherBuilder);
    private readonly EntitySubscriberBuilder<TEntity> _subBuilder = new(subscriberBuilder);

    public IEntityBuilder<TEntity> UseRepository(IRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _pubBuilder.UseRepository(repository);
        return this;
    }

    public IEntityBuilder<TEntity> UseOutbox(IOutbox outbox)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        _pubBuilder.UseOutbox(outbox);
        return this;
    }

    public IEntityBuilder<TEntity> UseQueue(IQueuePublisher queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _pubBuilder.UseQueue(queue);
        return this;
    }

    public IEntityBuilder<TEntity> UseSerializer(IChangeSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        _pubBuilder.UseSerializer(serializer);
        _subBuilder.UseSerializer(serializer);
        return this;
    }

    public IEntityBuilder<TEntity> UseCompressor(IChangeCompressor compressor)
    {
        ArgumentNullException.ThrowIfNull(compressor);
        _pubBuilder.UseCompressor(compressor);
        _subBuilder.UseCompressor(compressor);
        return this;
    }

    public IEntityBuilder<TEntity> UseConsumer(IQueueConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        _subBuilder.UseQueue(consumer);
        return this;
    }

    public IEntityBuilder<TEntity> UseSubscriberOptions(Action<SubscriberOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _subBuilder.UseOptions(configure);
        return this;
    }

    public IEntityBuilder<TEntity> OnInsert(ChangeHandlerAsync<TEntity> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _subBuilder.OnInsert(handler);
        return this;
    }

    public IEntityBuilder<TEntity> OnUpdate(ChangeHandlerAsync<TEntity> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _subBuilder.OnUpdate(handler);
        return this;
    }

    public IEntityBuilder<TEntity> OnDelete(ChangeHandlerAsync<TEntity> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _subBuilder.OnDelete(handler);
        return this;
    }

    public IEntityBuilder<TEntity> OnChange(ChangeType? changeType, ChangeHandlerAsync<TEntity> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _subBuilder.OnChange(changeType, handler);
        return this;
    }

    internal void RegisterSubscriberApplicator()
        => subscriberBuilder.AddEntityApplicator(_subBuilder.Apply);
}
