using RayTree.Core.Distribution;
using RayTree.Core.Handling;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Repository;
using RayTree.Core.Plugins.Serialization;

namespace RayTree.Core.Tracking;

/// <summary>
/// Internal implementation of <see cref="IEntityBuilder{TEntity}"/>. Owns the publisher
/// configuration and the shared sub-builder that carries serializer, compressor, and
/// subscriber-option overrides. When the caller binds a consumer — via
/// <see cref="UseConsumer"/> or <see cref="UseConsumerFactory"/> — the appropriate
/// post-fork builder is created and returned; <see cref="RegisterSubscriberApplicator"/>
/// later wires the chosen path into the parent <see cref="ChangeSubscriberBuilder"/>.
/// </summary>
internal sealed class EntityBuilder<TEntity>(ChangePublisherBuilder publisherBuilder, ChangeSubscriberBuilder subscriberBuilder)
    : IEntityBuilder<TEntity>
    where TEntity : class
{
    private readonly EntityPublisherBuilder<TEntity> _pubBuilder = new(publisherBuilder);
    private readonly EntitySubscriberBuilder<TEntity> _subBuilder = new(subscriberBuilder);

    // Exactly one of these is non-null once a consumer-binding method has been called.
    private SharedHandlerBuilder<TEntity>? _sharedBuilder;
    private IsolatedHandlerBuilder<TEntity>? _isolatedBuilder;

    // -------------------------------------------------------------------------
    // Publisher side
    // -------------------------------------------------------------------------

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

    public IEntityBuilder<TEntity> UsePublisher(IQueuePublisher queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _pubBuilder.UsePublisher(queue);
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

    // -------------------------------------------------------------------------
    // Subscriber side — pre-fork
    // -------------------------------------------------------------------------

    public IEntityBuilder<TEntity> UseSubscriberOptions(Action<SubscriberOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _subBuilder.UseOptions(configure);
        return this;
    }

    // -------------------------------------------------------------------------
    // Subscriber side — consumer binding (forks the builder chain)
    // -------------------------------------------------------------------------

    public ISharedHandlerBuilder<TEntity> UseConsumer(IQueueConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        _subBuilder.UseConsumer(consumer);
        _sharedBuilder = new SharedHandlerBuilder<TEntity>(_subBuilder);
        return _sharedBuilder;
    }

    public IIsolatedHandlerBuilder<TEntity> UseConsumerFactory(Func<string, IQueueConsumer> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _isolatedBuilder = new IsolatedHandlerBuilder<TEntity>(_subBuilder, factory);
        return _isolatedBuilder;
    }

    // -------------------------------------------------------------------------
    // Internal wiring
    // -------------------------------------------------------------------------

    internal void RegisterSubscriberApplicator()
    {
        if (_isolatedBuilder is not null)
            subscriberBuilder.AddEntityApplicator(_isolatedBuilder.Apply);
        else
            subscriberBuilder.AddEntityApplicator(_subBuilder.Apply);
    }
}
