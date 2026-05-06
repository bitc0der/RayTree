using System.IO.Pipelines;
using System.Collections.Concurrent;
using RayTree.Models;
using RayTree.Plugins;

namespace RayTree.Tracking;

public sealed class EntityChangeTracker : IEntityChangeTracker
{
    private readonly ConcurrentDictionary<Type, IOutbox> _outboxes = new();
    private readonly ConcurrentDictionary<Type, IQueuePublisher> _publishers = new();
    private readonly ConcurrentDictionary<Type, IChangeSerializer> _serializers = new();
    private readonly ConcurrentDictionary<Type, IChangeCompressor> _compressors = new();
    private readonly ConcurrentDictionary<Type, IRepository> _repositories = new();

    public void RegisterOutbox(Type entityType, IOutbox outbox) => _outboxes[entityType] = outbox;
    public void RegisterPublisher(Type entityType, IQueuePublisher publisher) => _publishers[entityType] = publisher;
    public void RegisterSerializer(Type entityType, IChangeSerializer serializer) => _serializers[entityType] = serializer;
    public void RegisterCompressor(Type entityType, IChangeCompressor compressor) => _compressors[entityType] = compressor;
    public void RegisterRepository(Type entityType, IRepository repository) => _repositories[entityType] = repository;

    public IOutbox GetOutbox(Type entityType) => _outboxes[entityType];
    public IQueuePublisher GetPublisher(Type entityType) => _publishers[entityType];
    public IChangeSerializer GetSerializer(Type entityType) => _serializers[entityType];
    public IChangeCompressor GetCompressor(Type entityType) => _compressors[entityType];
    public IRepository GetRepository(Type entityType) => _repositories[entityType];
    public IReadOnlyDictionary<Type, IOutbox> GetOutboxes() => _outboxes;
    public IReadOnlyDictionary<Type, IQueuePublisher> GetPublishers() => _publishers;
    public IReadOnlyDictionary<Type, IRepository> GetRepositories() => _repositories;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var initTasks = new List<Task>();

        foreach (var repo in _repositories.Values)
        {
            initTasks.Add(repo.InitializeAsync(cancellationToken));
        }

        foreach (var outbox in _outboxes.Values)
        {
            initTasks.Add(outbox.InitializeAsync(cancellationToken));
        }

        foreach (var publisher in _publishers.Values)
        {
            initTasks.Add(publisher.InitializeAsync(cancellationToken));
        }

        await Task.WhenAll(initTasks);
    }

    public async Task TrackChangeAsync(EntityChange change, CancellationToken cancellationToken = default)
    {
        var entityType = Type.GetType(change.EntityType) ?? throw new InvalidOperationException($"Unknown entity type: {change.EntityType}");

        var outbox = _outboxes.GetValueOrDefault(entityType) ?? throw new InvalidOperationException($"No outbox registered for {change.EntityType}");
        var publisher = _publishers.GetValueOrDefault(entityType);
        var serializer = _serializers.GetValueOrDefault(entityType);
        var compressor = _compressors.GetValueOrDefault(entityType);

        await outbox.WriteAsync(change, cancellationToken);

        if (publisher != null && serializer != null && compressor != null)
        {
            await PublishAsync(change, publisher, serializer, compressor, cancellationToken);
        }
    }

    public async Task TrackChangeAsync<TEntity>(EntityChange<TEntity> change, CancellationToken cancellationToken = default)
    {
        var entityType = typeof(TEntity);

        var outbox = _outboxes.GetValueOrDefault(entityType) ?? throw new InvalidOperationException($"No outbox registered for {entityType.Name}");
        var publisher = _publishers.GetValueOrDefault(entityType);
        var serializer = _serializers.GetValueOrDefault(entityType);
        var compressor = _compressors.GetValueOrDefault(entityType);

        await outbox.WriteAsync(change, cancellationToken);

        if (publisher != null && serializer != null && compressor != null)
        {
            await PublishAsync(change, publisher, serializer, compressor, cancellationToken);
        }
    }

    public async Task<EntityChange<TEntity>> TrackInsertAsync<TEntity>(TEntity entity, CancellationToken cancellationToken = default) where TEntity : class
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

    public async Task<EntityChange<TEntity>> TrackUpdateAsync<TEntity>(TEntity entity, CancellationToken cancellationToken = default) where TEntity : class
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

    public async Task<EntityChange<TEntity>> TrackDeleteAsync<TEntity>(TEntity entity, CancellationToken cancellationToken = default) where TEntity : class
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

    private static string GetEntityId<TEntity>(TEntity entity)
    {
        var idProperty = typeof(TEntity).GetProperty("Id") ?? throw new InvalidOperationException($"Entity {typeof(TEntity).Name} must have an Id property");
        return idProperty.GetValue(entity)?.ToString() ?? throw new InvalidOperationException("Id cannot be null");
    }

    public async Task TrackChangesAsync(IEnumerable<EntityChange> changes, CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid();
        var tasks = changes.Select(async change =>
        {
            change.CorrelationId = correlationId;
            await TrackChangeAsync(change, cancellationToken);
        });

        await Task.WhenAll(tasks);
    }

    private static async Task PublishAsync(EntityChange change, IQueuePublisher publisher, IChangeSerializer serializer, IChangeCompressor compressor, CancellationToken ct)
    {
        var serializePipe = new Pipe();
        var compressPipe = new Pipe();

        var serializeTask = serializer.SerializeAsync(change, serializePipe.Writer, ct);
        var compressTask = compressor.CompressAsync(serializePipe.Reader, compressPipe.Writer, ct);
        var publishTask = publisher.PublishAsync(change, compressPipe.Reader, ct);

        await Task.WhenAll(serializeTask, compressTask, publishTask);
    }

    public void Dispose()
    {
    }
}
