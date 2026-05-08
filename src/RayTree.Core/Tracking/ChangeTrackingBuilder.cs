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
    private readonly Dictionary<Type, IOutbox> _outboxOverrides = new();
    private readonly Dictionary<Type, IQueuePublisher> _queueOverrides = new();
    private readonly Dictionary<Type, IChangeSerializer> _serializerOverrides = new();
    private readonly Dictionary<Type, IChangeCompressor> _compressorOverrides = new();
    private readonly Dictionary<Type, IRepository> _repositoryOverrides = new();

    private Action<OutboxPublisherOptions>? _publisherOptionsConfigure;

    private Func<Type, IOutbox>? _outboxFactory;
    private Func<Type, IQueuePublisher>? _queueFactory;
    private Func<Type, IChangeSerializer>? _serializerFactory;
    private Func<Type, IChangeCompressor>? _compressorFactory;
    private Func<Type, IRepository>? _repositoryFactory;

    private readonly ChangeSubscriberBuilder _subscriberBuilder = new();

    public IChangeTrackingBuilder UseOutbox<T>(Func<Type, IOutbox> factory)
        where T : IOutbox
    {
        _outboxFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public IChangeTrackingBuilder UseQueue<T>(Func<Type, IQueuePublisher> factory)
        where T : IQueuePublisher
    {
        _queueFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public IChangeTrackingBuilder UseSerializer<T>(Func<Type, IChangeSerializer> factory)
        where T : IChangeSerializer
    {
        ArgumentNullException.ThrowIfNull(factory);
        _serializerFactory = factory;
        _subscriberBuilder.UseSerializer(factory(typeof(object)));
        return this;
    }

    public IChangeTrackingBuilder UseCompressor<T>(Func<Type, IChangeCompressor> factory)
        where T : IChangeCompressor
    {
        ArgumentNullException.ThrowIfNull(factory);
        _compressorFactory = factory;
        _subscriberBuilder.UseCompressor(factory(typeof(object)));
        return this;
    }

    public IChangeTrackingBuilder UseRepository<T>(Func<Type, IRepository> factory)
        where T : IRepository
    {
        _repositoryFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public IChangeTrackingBuilder UsePublisherOptions(Action<OutboxPublisherOptions> configure)
    {
        _publisherOptionsConfigure = configure ?? throw new ArgumentNullException(nameof(configure));
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
        var entityBuilder = new EntityBuilder<TEntity>(this, _subscriberBuilder);
        configure(entityBuilder);
        entityBuilder.RegisterSubscriberApplicator();
        return this;
    }

    internal void AddOutboxOverride(Type entityType, IOutbox outbox) => _outboxOverrides[entityType] = outbox;

    internal void AddQueueOverride(Type entityType, IQueuePublisher queue) => _queueOverrides[entityType] = queue;

    internal void AddSerializerOverride(Type entityType, IChangeSerializer serializer) =>
        _serializerOverrides[entityType] = serializer;

    internal void AddCompressorOverride(Type entityType, IChangeCompressor compressor) =>
        _compressorOverrides[entityType] = compressor;

    internal void AddRepositoryOverride(Type entityType, IRepository repository) =>
        _repositoryOverrides[entityType] = repository;

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
        var publisher = new ChangePublisher();
        _publisherOptionsConfigure?.Invoke(publisher.Options);

        var entityTypes = _outboxOverrides.Keys
            .Concat(_queueOverrides.Keys)
            .Concat(_serializerOverrides.Keys)
            .Concat(_compressorOverrides.Keys)
            .Concat(_repositoryOverrides.Keys)
            .Distinct();

        foreach (var entityType in entityTypes)
        {
            var outbox = _outboxOverrides.GetValueOrDefault(entityType) ?? _outboxFactory?.Invoke(entityType)
                ?? throw new InvalidOperationException($"No outbox configured for {entityType.Name}");

            var queue = _queueOverrides.GetValueOrDefault(entityType) ?? _queueFactory?.Invoke(entityType)
                ?? throw new InvalidOperationException($"No queue configured for {entityType.Name}");

            var serializer = _serializerOverrides.GetValueOrDefault(entityType) ??
                             _serializerFactory?.Invoke(entityType)
                             ?? throw new InvalidOperationException($"No serializer configured for {entityType.Name}");

            var compressor = _compressorOverrides.GetValueOrDefault(entityType) ??
                             _compressorFactory?.Invoke(entityType)
                             ?? throw new InvalidOperationException($"No compressor configured for {entityType.Name}");

            var repository = _repositoryOverrides.GetValueOrDefault(entityType) ??
                             _repositoryFactory?.Invoke(entityType);

            publisher.RegisterOutbox(entityType, outbox);
            publisher.RegisterPublisher(entityType, queue);
            publisher.RegisterSerializer(entityType, serializer);
            publisher.RegisterCompressor(entityType, compressor);

            if (repository != null)
                publisher.RegisterRepository(entityType, repository);
        }

        var subscriber = _subscriberBuilder.Build();
        return new EntityChangeTracker(publisher, subscriber);
    }
}
