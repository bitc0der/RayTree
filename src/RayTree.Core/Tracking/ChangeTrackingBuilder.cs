using RayTree.Distribution;
using RayTree.Plugins;

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
        _serializerFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public IChangeTrackingBuilder UseCompressor<T>(Func<Type, IChangeCompressor> factory)
        where T : IChangeCompressor
    {
        _compressorFactory = factory ?? throw new ArgumentNullException(nameof(factory));
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

    public IEntityBuilder ForEntity<TEntity>()
    {
        return new EntityBuilder(this, typeof(TEntity));
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
        var tracker = new EntityChangeTracker();
        _publisherOptionsConfigure?.Invoke(tracker.PublisherOptions);

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

            tracker.RegisterOutbox(entityType, outbox);
            tracker.RegisterPublisher(entityType, queue);
            tracker.RegisterSerializer(entityType, serializer);
            tracker.RegisterCompressor(entityType, compressor);

            if (repository != null)
            {
                tracker.RegisterRepository(entityType, repository);
            }
        }

        return tracker;
    }
}
