using RayTree.Core.Distribution;
using RayTree.Core.Handling;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Consumer;

namespace RayTree.Core.Tracking;

public sealed class EntityChangeTracker : IEntityChangeTracker
{
    private readonly ChangePublisher _publisher;
    private readonly ChangeSubscriber? _subscriber;
    private bool _disposed;

    public ChangePublisher Publisher => _publisher;
    public ChangeSubscriber? Subscriber => _subscriber;

    public EntityChangeTracker(ChangePublisher publisher, ChangeSubscriber? subscriber = null)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _subscriber = subscriber;
    }

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
        var outbox = _publisher.GetOutbox(typeof(TEntity));
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
