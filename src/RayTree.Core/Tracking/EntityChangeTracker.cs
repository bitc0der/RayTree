using System.Collections.Concurrent;
using System.IO.Pipelines;
using RayTree.Core.Distribution;
using RayTree.Core.Models;
using RayTree.Core.Plugins;
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

    public OutboxPublisherOptions PublisherOptions { get; } = new();

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
            var service = new OutboxPublisherService(this, entityType, PublisherOptions);
            _publisherServices.Add(service);
            await service.StartAsync(cancellationToken);
        }
    }

    public async Task TrackChangeAsync<TEntity>(
        TEntity entity,
        ChangeType changeType,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
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

    public async Task PublishAsync<TEntity>(
        EntityChange<TEntity> change,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var entityType = typeof(TEntity);

        var publisher = GetPublisher(entityType);
        var serializer = GetSerializer(entityType);
        var compressor = GetCompressor(entityType);

        var serializePipe = new Pipe();
        var compressPipe = new Pipe();

        var serializeTask = serializer.SerializeAsync(change, serializePipe.Writer, cancellationToken);
        var compressTask = compressor.CompressAsync(serializePipe.Reader, compressPipe.Writer, cancellationToken);
        var publishTask = publisher.PublishAsync(change, compressPipe.Reader, cancellationToken);

        await Task.WhenAll(serializeTask, compressTask, publishTask);
    }

    public void Dispose()
    {
        foreach (var service in _publisherServices)
        {
            service.StopAsync().GetAwaiter().GetResult();
            service.Dispose();
        }
    }
}
