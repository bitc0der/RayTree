using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Repository;
using RayTree.Core.Plugins.Serialization;

namespace RayTree.Core.Distribution;

/// <summary>
/// Owns all publisher-side plugin registrations and the <see cref="OutboxPublisherService"/>
/// instances that drive them. Parallel to <see cref="RayTree.Core.Handling.ChangeSubscriber"/>
/// on the subscriber side.
/// </summary>
public sealed class ChangePublisher : IDisposable
{
    private readonly ConcurrentDictionary<Type, IOutbox> _outboxes = new();
    private readonly ConcurrentDictionary<Type, IQueuePublisher> _publishers = new();
    private readonly ConcurrentDictionary<Type, IChangeSerializer> _serializers = new();
    private readonly ConcurrentDictionary<Type, IChangeCompressor> _compressors = new();
    private readonly ConcurrentDictionary<Type, IRepository> _repositories = new();
    private readonly List<OutboxPublisherService> _publisherServices = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ChangePublisher> _logger;

    public OutboxPublisherOptions Options { get; } = new();

    public ChangePublisher(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger        = loggerFactory.CreateLogger<ChangePublisher>();
    }

    public void RegisterOutbox(Type entityType, IOutbox outbox) => _outboxes[entityType] = outbox;
    public void RegisterPublisher(Type entityType, IQueuePublisher publisher) => _publishers[entityType] = publisher;
    public void RegisterSerializer(Type entityType, IChangeSerializer serializer) => _serializers[entityType] = serializer;
    public void RegisterCompressor(Type entityType, IChangeCompressor compressor) => _compressors[entityType] = compressor;
    public void RegisterRepository(Type entityType, IRepository repository) => _repositories[entityType] = repository;

    public IOutbox GetOutbox(Type entityType) =>
        _outboxes.TryGetValue(entityType, out var v) ? v
            : throw new InvalidOperationException($"No outbox registered for {entityType.Name}");

    public IQueuePublisher GetPublisher(Type entityType) =>
        _publishers.TryGetValue(entityType, out var v) ? v
            : throw new InvalidOperationException($"No publisher registered for {entityType.Name}");

    public IChangeSerializer GetSerializer(Type entityType) =>
        _serializers.TryGetValue(entityType, out var v) ? v
            : throw new InvalidOperationException($"No serializer registered for {entityType.Name}");

    public IChangeCompressor GetCompressor(Type entityType) =>
        _compressors.TryGetValue(entityType, out var v) ? v
            : throw new InvalidOperationException($"No compressor registered for {entityType.Name}");

    public IRepository GetRepository(Type entityType) =>
        _repositories.TryGetValue(entityType, out var v) ? v
            : throw new InvalidOperationException($"No repository registered for {entityType.Name}");

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
            _logger.LogInformation("Registering outbox publisher service for {EntityType}", entityType.Name);
            var service = new OutboxPublisherService(this, entityType, Options, _loggerFactory);
            _publisherServices.Add(service);
            await service.StartAsync(cancellationToken);
        }
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
