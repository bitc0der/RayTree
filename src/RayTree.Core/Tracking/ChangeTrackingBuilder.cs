using RayTree.Core.Distribution;
using RayTree.Core.Handling;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Deduplication;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Repository;
using RayTree.Core.Plugins.Serialization;

namespace RayTree.Core.Tracking;

public class ChangeTrackingBuilder : IChangeTrackingBuilder
{
    private readonly ChangePublisherBuilder _publisherBuilder = new();
    private readonly ChangeSubscriberBuilder _subscriberBuilder = new();

    public IChangeTrackingBuilder UseOutbox<T>(Func<Type, IOutbox> factory) where T : IOutbox
    {
        _publisherBuilder.UseOutbox<T>(factory);
        return this;
    }

    public IChangeTrackingBuilder UseQueue<T>(Func<Type, IQueuePublisher> factory) where T : IQueuePublisher
    {
        _publisherBuilder.UseQueue<T>(factory);
        return this;
    }

    public IChangeTrackingBuilder UseSerializer<T>(Func<Type, IChangeSerializer> factory) where T : IChangeSerializer
    {
        ArgumentNullException.ThrowIfNull(factory);
        _publisherBuilder.UseSerializer<T>(factory);
        _subscriberBuilder.UseSerializer(factory(typeof(object)));
        return this;
    }

    public IChangeTrackingBuilder UseCompressor<T>(Func<Type, IChangeCompressor> factory) where T : IChangeCompressor
    {
        ArgumentNullException.ThrowIfNull(factory);
        _publisherBuilder.UseCompressor<T>(factory);
        _subscriberBuilder.UseCompressor(factory(typeof(object)));
        return this;
    }

    public IChangeTrackingBuilder UseRepository<T>(Func<Type, IRepository> factory) where T : IRepository
    {
        _publisherBuilder.UseRepository<T>(factory);
        return this;
    }

    public IChangeTrackingBuilder UsePublisherOptions(Action<OutboxPublisherOptions> configure)
    {
        _publisherBuilder.UseOptions(configure);
        return this;
    }

    public IChangeTrackingBuilder UseSubscriberOptions(Action<SubscriberOptions> configure)
    {
        _subscriberBuilder.UseOptions(configure);
        return this;
    }

    public IChangeTrackingBuilder UseDeduplicationStore(IDeduplicationStore store)
    {
        _subscriberBuilder.UseDeduplicationStore(store);
        return this;
    }

    public IChangeTrackingBuilder ForEntity<TEntity>(Action<IEntityBuilder<TEntity>> configure)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        var entityBuilder = new EntityBuilder<TEntity>(_publisherBuilder, _subscriberBuilder);
        configure(entityBuilder);
        entityBuilder.RegisterSubscriberApplicator();
        return this;
    }

    public EntityChangeTracker Build()
    {
        var tracker = BuildInternal();
        tracker.InitializeAsync().GetAwaiter().GetResult();
        return tracker;
    }

    public async Task<EntityChangeTracker> BuildAsync(CancellationToken cancellationToken = default)
    {
        var tracker = BuildInternal();
        await tracker.InitializeAsync(cancellationToken);
        return tracker;
    }

    private EntityChangeTracker BuildInternal()
    {
        var publisher = _publisherBuilder.Build();
        var subscriber = _subscriberBuilder.Build();
        return new EntityChangeTracker(publisher, subscriber);
    }
}
