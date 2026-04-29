using RayTree.Tracking;

namespace RayTree.Plugins;

public interface IChangeTrackingBuilder
{
    IChangeTrackingBuilder UseOutbox<T>(Func<Type, IOutbox> factory) where T : IOutbox;
    IChangeTrackingBuilder UseQueue<T>(Func<Type, IQueuePublisher> factory) where T : IQueuePublisher;
    IChangeTrackingBuilder UseSerializer<T>(Func<Type, IChangeSerializer> factory) where T : IChangeSerializer;
    IChangeTrackingBuilder UseCompressor<T>(Func<Type, IChangeCompressor> factory) where T : IChangeCompressor;
    IEntityBuilder ForEntity<TEntity>();
    EntityChangeTracker Build();
}

public interface IEntityBuilder
{
    IEntityBuilder UseOutbox(IOutbox outbox);
    IEntityBuilder UseQueue(IQueuePublisher queue);
    IEntityBuilder UseSerializer(IChangeSerializer serializer);
    IEntityBuilder UseCompressor(IChangeCompressor compressor);
}

internal class ChangeTrackingBuilder : IChangeTrackingBuilder
{
    private readonly Dictionary<Type, IOutbox> _outboxOverrides = new();
    private readonly Dictionary<Type, IQueuePublisher> _queueOverrides = new();
    private readonly Dictionary<Type, IChangeSerializer> _serializerOverrides = new();
    private readonly Dictionary<Type, IChangeCompressor> _compressorOverrides = new();

    private Func<Type, IOutbox>? _outboxFactory;
    private Func<Type, IQueuePublisher>? _queueFactory;
    private Func<Type, IChangeSerializer>? _serializerFactory;
    private Func<Type, IChangeCompressor>? _compressorFactory;

    public IChangeTrackingBuilder UseOutbox<T>(Func<Type, IOutbox> factory) where T : IOutbox
    {
        _outboxFactory = factory;
        return this;
    }

    public IChangeTrackingBuilder UseQueue<T>(Func<Type, IQueuePublisher> factory) where T : IQueuePublisher
    {
        _queueFactory = factory;
        return this;
    }

    public IChangeTrackingBuilder UseSerializer<T>(Func<Type, IChangeSerializer> factory) where T : IChangeSerializer
    {
        _serializerFactory = factory;
        return this;
    }

    public IChangeTrackingBuilder UseCompressor<T>(Func<Type, IChangeCompressor> factory) where T : IChangeCompressor
    {
        _compressorFactory = factory;
        return this;
    }

    public IEntityBuilder ForEntity<TEntity>()
    {
        return new EntityBuilder(this, typeof(TEntity));
    }

    internal void AddOutboxOverride(Type entityType, IOutbox outbox) => _outboxOverrides[entityType] = outbox;
    internal void AddQueueOverride(Type entityType, IQueuePublisher queue) => _queueOverrides[entityType] = queue;
    internal void AddSerializerOverride(Type entityType, IChangeSerializer serializer) => _serializerOverrides[entityType] = serializer;
    internal void AddCompressorOverride(Type entityType, IChangeCompressor compressor) => _compressorOverrides[entityType] = compressor;

    public EntityChangeTracker Build()
    {
        var tracker = new EntityChangeTracker();

        var entityTypes = _outboxOverrides.Keys
            .Concat(_queueOverrides.Keys)
            .Concat(_serializerOverrides.Keys)
            .Concat(_compressorOverrides.Keys)
            .Distinct();

        foreach (var entityType in entityTypes)
        {
            var outbox = _outboxOverrides.GetValueOrDefault(entityType) ?? _outboxFactory?.Invoke(entityType)
                ?? throw new InvalidOperationException($"No outbox configured for {entityType.Name}");

            var queue = _queueOverrides.GetValueOrDefault(entityType) ?? _queueFactory?.Invoke(entityType)
                ?? throw new InvalidOperationException($"No queue configured for {entityType.Name}");

            var serializer = _serializerOverrides.GetValueOrDefault(entityType) ?? _serializerFactory?.Invoke(entityType)
                ?? throw new InvalidOperationException($"No serializer configured for {entityType.Name}");

            var compressor = _compressorOverrides.GetValueOrDefault(entityType) ?? _compressorFactory?.Invoke(entityType)
                ?? throw new InvalidOperationException($"No compressor configured for {entityType.Name}");

            tracker.RegisterOutbox(entityType, outbox);
            tracker.RegisterPublisher(entityType, queue);
            tracker.RegisterSerializer(entityType, serializer);
            tracker.RegisterCompressor(entityType, compressor);
        }

        return tracker;
    }
}

internal class EntityBuilder : IEntityBuilder
{
    private readonly ChangeTrackingBuilder _parent;
    private readonly Type _entityType;

    public EntityBuilder(ChangeTrackingBuilder parent, Type entityType)
    {
        _parent = parent;
        _entityType = entityType;
    }

    public IEntityBuilder UseOutbox(IOutbox outbox)
    {
        _parent.AddOutboxOverride(_entityType, outbox);
        return this;
    }

    public IEntityBuilder UseQueue(IQueuePublisher queue)
    {
        _parent.AddQueueOverride(_entityType, queue);
        return this;
    }

    public IEntityBuilder UseSerializer(IChangeSerializer serializer)
    {
        _parent.AddSerializerOverride(_entityType, serializer);
        return this;
    }

    public IEntityBuilder UseCompressor(IChangeCompressor compressor)
    {
        _parent.AddCompressorOverride(_entityType, compressor);
        return this;
    }
}
