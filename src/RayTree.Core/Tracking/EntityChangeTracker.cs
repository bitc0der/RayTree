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

    public void RegisterOutbox(Type entityType, IOutbox outbox) => _outboxes[entityType] = outbox;
    public void RegisterPublisher(Type entityType, IQueuePublisher publisher) => _publishers[entityType] = publisher;
    public void RegisterSerializer(Type entityType, IChangeSerializer serializer) => _serializers[entityType] = serializer;
    public void RegisterCompressor(Type entityType, IChangeCompressor compressor) => _compressors[entityType] = compressor;

    public IOutbox GetOutbox(Type entityType) => _outboxes[entityType];
    public IQueuePublisher GetPublisher(Type entityType) => _publishers[entityType];
    public IChangeSerializer GetSerializer(Type entityType) => _serializers[entityType];
    public IChangeCompressor GetCompressor(Type entityType) => _compressors[entityType];
    public IReadOnlyDictionary<Type, IOutbox> GetOutboxes() => _outboxes;

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
