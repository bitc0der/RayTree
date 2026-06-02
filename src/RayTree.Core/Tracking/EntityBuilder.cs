using Microsoft.Extensions.Logging;
using RayTree.Core.Distribution;
using RayTree.Core.Handling;
using RayTree.Core.Plugins.Compression;
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
internal sealed class EntityBuilder<TEntity> : IEntityBuilder<TEntity>
    where TEntity : class
{
    private readonly ChangeSubscriberBuilder _subscriberBuilder;
    private readonly IEntityPublisherBuilder<TEntity> _pubBuilder;
    private readonly EntitySubscriberBuilder<TEntity> _subBuilder;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<EntityBuilder<TEntity>> _logger;
    private static readonly string EntityTypeName = typeof(TEntity).Name;

    // Exactly one of these is non-null once a consumer-binding method has been called.
    private SharedHandlerBuilder<TEntity>? _sharedBuilder;
    private IsolatedHandlerBuilder<TEntity>? _isolatedBuilder;

    internal EntityBuilder(
        ChangePublisherBuilder publisherBuilder,
        ChangeSubscriberBuilder subscriberBuilder,
        ILoggerFactory loggerFactory)
    {
        _subscriberBuilder = subscriberBuilder;
        _pubBuilder = new EntityPublisherBuilder<TEntity>(publisherBuilder);
        _subBuilder = new EntitySubscriberBuilder<TEntity>(subscriberBuilder);
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<EntityBuilder<TEntity>>();
    }

    private void LogOverride(string slot, string pluginName)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug(
                "ChangeTracking: entity override applied EntityType={EntityType} Override={Override} Plugin={Plugin}",
                EntityTypeName, slot, pluginName);
    }

    // -------------------------------------------------------------------------
    // Publisher side
    // -------------------------------------------------------------------------

    public IEntityBuilder<TEntity> UseRepository(IRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _pubBuilder.UseRepository(repository);
        LogOverride("Repository", repository.GetType().Name);
        return this;
    }

    public IEntityBuilder<TEntity> UseOutbox(IOutbox outbox)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        _pubBuilder.UseOutbox(outbox);
        LogOverride("Outbox", outbox.GetType().Name);
        return this;
    }

    public IEntityBuilder<TEntity> UsePublisher(IQueuePublisher queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _pubBuilder.UsePublisher(queue);
        LogOverride("Publisher", queue.GetType().Name);
        return this;
    }

    public IEntityBuilder<TEntity> UseSerializer(IChangeSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        _pubBuilder.UseSerializer(serializer);
        _subBuilder.UseSerializer(serializer);
        LogOverride("Serializer", serializer.GetType().Name);
        return this;
    }

    public IEntityBuilder<TEntity> UseCompressor(IChangeCompressor compressor)
    {
        ArgumentNullException.ThrowIfNull(compressor);
        _pubBuilder.UseCompressor(compressor);
        _subBuilder.UseCompressor(compressor);
        LogOverride("Compressor", compressor.GetType().Name);
        return this;
    }

    // -------------------------------------------------------------------------
    // Subscriber side — pre-fork
    // -------------------------------------------------------------------------

    public IEntityBuilder<TEntity> UseSubscriberOptions(Action<SubscriberOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _subBuilder.UseOptions(configure);
        LogOverride("SubscriberOptions", nameof(SubscriberOptions));
        return this;
    }

    // -------------------------------------------------------------------------
    // Subscriber side — consumer binding (forks the builder chain)
    // -------------------------------------------------------------------------

    public ISharedHandlerBuilder<TEntity> UseConsumer(IQueueConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        _subBuilder.UseConsumer(consumer);
        LogOverride("Consumer", consumer.GetType().Name);
        _sharedBuilder = new SharedHandlerBuilder<TEntity>(_subBuilder, _loggerFactory);
        return _sharedBuilder;
    }

    public IIsolatedHandlerBuilder<TEntity> UseConsumerFactory(Func<string, IQueueConsumer> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        LogOverride("ConsumerFactory", factory.GetType().Name);
        _isolatedBuilder = new IsolatedHandlerBuilder<TEntity>(_subBuilder, factory, _loggerFactory);
        return _isolatedBuilder;
    }

    // -------------------------------------------------------------------------
    // Internal wiring
    // -------------------------------------------------------------------------

    internal void RegisterSubscriberApplicator()
    {
        if (_isolatedBuilder is not null)
            _subscriberBuilder.AddEntityApplicator(_isolatedBuilder.Apply);
        else
            _subscriberBuilder.AddEntityApplicator(_subBuilder.Apply);
    }
}
