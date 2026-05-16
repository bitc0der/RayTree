using RayTree.Core.Distribution;
using RayTree.Core.Handling;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Telemetry;

namespace RayTree.Core.Tracking;

public sealed class EntityChangeTracker : IEntityChangeTracker
{
    private readonly ChangePublisher _publisher;
    private readonly ChangeSubscriber? _subscriber;
    private readonly RayTreeMeter _meter;
    private readonly bool _ownsMeter;
    private bool _disposed;

    public ChangePublisher Publisher => _publisher;
    public ChangeSubscriber? Subscriber => _subscriber;

    /// <summary>The meter used by this tracker's publisher and subscriber.</summary>
    public RayTreeMeter Meter => _meter;

    /// <summary>
    /// Constructs a tracker. When <paramref name="ownsMeter"/> is <c>true</c>,
    /// <see cref="Dispose"/> also disposes <paramref name="meter"/>. Builders that create
    /// the meter on the caller's behalf should pass <c>ownsMeter: true</c>; callers that
    /// inject their own meter via <c>UseMeter</c> should pass <c>false</c>.
    /// </summary>
    public EntityChangeTracker(
        ChangePublisher publisher,
        ChangeSubscriber? subscriber = null,
        RayTreeMeter? meter = null,
        bool ownsMeter = false)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _subscriber = subscriber;
        _meter      = meter ?? publisher.Meter;
        _ownsMeter  = ownsMeter;
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

        _meter.OutboxWrites.Add(1,
            RayTreeMeter.EntityTag(typeof(TEntity)),
            RayTreeMeter.ChangeTag(change.ChangeType));
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
        if (_ownsMeter) _meter.Dispose();
    }
}
