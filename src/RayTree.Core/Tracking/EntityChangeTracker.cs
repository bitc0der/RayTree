using RayTree.Core.Distribution;
using RayTree.Core.Handling;
using RayTree.Core.Models;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Repository;
using RayTree.Core.Plugins.Serialization;

namespace RayTree.Core.Tracking;

public sealed class EntityChangeTracker : IEntityChangeTracker
{
    private readonly ChangePublisher _publisher;
    private ChangeSubscriber? _subscriber;
    private bool _disposed;

    public EntityChangeTracker() : this(new ChangePublisher()) { }

    public EntityChangeTracker(ChangePublisher publisher)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    }

    public ChangePublisher Publisher => _publisher;

    public OutboxPublisherOptions PublisherOptions => _publisher.Options;

    public IReadOnlyDictionary<Type, IQueueConsumer> Consumers
        => _subscriber?.Queues ?? new Dictionary<Type, IQueueConsumer>();

    internal void AttachSubscriber(ChangeSubscriber subscriber) => _subscriber = subscriber;

    public void RegisterOutbox(Type entityType, IOutbox outbox) => _publisher.RegisterOutbox(entityType, outbox);
    public void RegisterPublisher(Type entityType, IQueuePublisher publisher) => _publisher.RegisterPublisher(entityType, publisher);
    public void RegisterSerializer(Type entityType, IChangeSerializer serializer) => _publisher.RegisterSerializer(entityType, serializer);
    public void RegisterCompressor(Type entityType, IChangeCompressor compressor) => _publisher.RegisterCompressor(entityType, compressor);
    public void RegisterRepository(Type entityType, IRepository repository) => _publisher.RegisterRepository(entityType, repository);

    public IOutbox GetOutbox(Type entityType) => _publisher.GetOutbox(entityType);
    public IQueuePublisher GetPublisher(Type entityType) => _publisher.GetPublisher(entityType);
    public IChangeSerializer GetSerializer(Type entityType) => _publisher.GetSerializer(entityType);
    public IChangeCompressor GetCompressor(Type entityType) => _publisher.GetCompressor(entityType);
    public IRepository GetRepository(Type entityType) => _publisher.GetRepository(entityType);

    public IReadOnlyDictionary<Type, IOutbox> GetOutboxes() => _publisher.GetOutboxes();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _publisher.InitializeAsync(cancellationToken);

        if (_subscriber != null)
        {
            foreach (var (_, consumer) in _subscriber.Queues)
                await consumer.InitializeAsync(cancellationToken);
        }
    }

    public Task ConsumeFromConsumerAsync(IQueueConsumer consumer, CancellationToken cancellationToken = default)
        => _subscriber!.ConsumeFromConsumerAsync(consumer, cancellationToken);

    public Task ProcessMessageAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
        => _subscriber!.ProcessMessageAsync(envelope, cancellationToken);

    public async Task TrackChangeAsync<TEntity>(
        TEntity entity,
        ChangeType changeType,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);

        var change = new EntityChange<TEntity>
        {
            EntityType = typeof(TEntity).FullName!,
            EntityId = GetEntityId(entity),
            ChangeType = changeType,
            State = entity
        };

        await TrackChangeAsync(change, cancellationToken);
    }

    public async Task<EntityChange<TEntity>> TrackInsertAsync<TEntity>(
        TEntity entity,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var change = new EntityChange<TEntity>
        {
            EntityType = typeof(TEntity).FullName!,
            EntityId = GetEntityId(entity),
            ChangeType = ChangeType.Insert,
            State = entity
        };

        await TrackChangeAsync(change, cancellationToken);

        return change;
    }

    public async Task<EntityChange<TEntity>> TrackUpdateAsync<TEntity>(
        TEntity entity,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var change = new EntityChange<TEntity>
        {
            EntityType = typeof(TEntity).FullName!,
            EntityId = GetEntityId(entity),
            ChangeType = ChangeType.Update,
            State = entity
        };

        await TrackChangeAsync(change, cancellationToken);

        return change;
    }

    public async Task<EntityChange<TEntity>> TrackDeleteAsync<TEntity>(
        TEntity entity,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var change = new EntityChange<TEntity>
        {
            EntityType = typeof(TEntity).FullName!,
            EntityId = GetEntityId(entity),
            ChangeType = ChangeType.Delete,
            State = entity
        };

        await TrackChangeAsync(change, cancellationToken);

        return change;
    }

    public async Task TrackChangeAsync<TEntity>(
        EntityChange<TEntity> change,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var entityType = typeof(TEntity);

        var outbox = _publisher.GetOutbox(entityType);

        await outbox.WriteAsync(change, cancellationToken);
    }

    private static string GetEntityId<TEntity>(TEntity entity)
    {
        var idProperty = typeof(TEntity).GetProperty("Id") ??
                         throw new InvalidOperationException($"Entity {typeof(TEntity).Name} must have an Id property");

        return idProperty.GetValue(entity)?.ToString() ?? throw new InvalidOperationException("Id cannot be null");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _publisher.Dispose();
        _subscriber?.Dispose();
    }
}
