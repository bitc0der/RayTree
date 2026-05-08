using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<Type, IOutbox> _outboxes = new();
    private readonly ConcurrentDictionary<Type, IQueuePublisher> _publishers = new();
    private readonly ConcurrentDictionary<Type, IChangeSerializer> _serializers = new();
    private readonly ConcurrentDictionary<Type, IChangeCompressor> _compressors = new();
    private readonly ConcurrentDictionary<Type, IRepository> _repositories = new();

    private readonly List<OutboxPublisherService> _publisherServices = new();
    private ChangeSubscriber? _subscriber;
    private bool _disposed;

    public OutboxPublisherOptions PublisherOptions { get; } = new();

    /// <summary>Exposes the consumer queues registered on the attached subscriber.</summary>
    public IReadOnlyDictionary<Type, IQueueConsumer> Consumers
        => _subscriber?.Queues ?? new Dictionary<Type, IQueueConsumer>();

    internal void AttachSubscriber(ChangeSubscriber subscriber) => _subscriber = subscriber;

    public void RegisterOutbox(Type entityType, IOutbox outbox) => _outboxes[entityType] = outbox;

    public void RegisterPublisher(Type entityType, IQueuePublisher publisher) => _publishers[entityType] = publisher;

    public void RegisterSerializer(Type entityType, IChangeSerializer serializer) =>
        _serializers[entityType] = serializer;

    public void RegisterCompressor(Type entityType, IChangeCompressor compressor) =>
        _compressors[entityType] = compressor;

    public void RegisterRepository(Type entityType, IRepository repository) => _repositories[entityType] = repository;

    public IOutbox GetOutbox(Type entityType) => _outboxes[entityType];
    public IQueuePublisher GetPublisher(Type entityType) => _publishers[entityType];
    public IChangeSerializer GetSerializer(Type entityType) => _serializers[entityType];
    public IChangeCompressor GetCompressor(Type entityType) => _compressors[entityType];
    public IRepository GetRepository(Type entityType) => _repositories[entityType];

    public IReadOnlyDictionary<Type, IOutbox> GetOutboxes() => _outboxes;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        foreach (var repo in _repositories.Values)
            await repo.InitializeAsync(cancellationToken);

        foreach (var outbox in _outboxes.Values)
            await outbox.InitializeAsync(cancellationToken);

        foreach (var publisher in _publishers.Values)
            await publisher.InitializeAsync(cancellationToken);

        foreach (var entityType in _publishers.Keys)
        {
            var service = new OutboxPublisherService(tracker: this, entityType, PublisherOptions);
            _publisherServices.Add(service);
            await service.StartAsync(cancellationToken);
        }

        if (_subscriber != null)
        {
            foreach (var (_, consumer) in _subscriber.Queues)
                await consumer.InitializeAsync(cancellationToken);
        }
    }

    /// <summary>Starts a consume loop for the given consumer, delegating to the attached subscriber.</summary>
    public Task ConsumeFromConsumerAsync(IQueueConsumer consumer, CancellationToken cancellationToken = default)
        => _subscriber!.ConsumeFromConsumerAsync(consumer, cancellationToken);

    /// <summary>Processes a single message envelope through the attached subscriber.</summary>
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

        var outbox = _outboxes.GetValueOrDefault(entityType) ??
                     throw new InvalidOperationException($"No outbox registered for {entityType.Name}");

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

        foreach (var service in _publisherServices)
        {
            service.StopAsync().GetAwaiter().GetResult();
            service.Dispose();
        }

        _subscriber?.Dispose();
    }
}
