using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Repository;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Telemetry;

namespace RayTree.Core.Distribution;

/// <summary>
/// Standalone fluent builder that produces a <see cref="ChangePublisher"/> with global
/// defaults and optional per-entity overrides. Parallel to
/// <see cref="RayTree.Core.Handling.ChangeSubscriberBuilder"/> on the subscriber side.
/// </summary>
public sealed class ChangePublisherBuilder : IChangePublisherBuilder
{
    private readonly Dictionary<Type, IOutbox> _outboxOverrides = new();
    private readonly Dictionary<Type, IQueuePublisher> _queueOverrides = new();
    private readonly Dictionary<Type, IChangeSerializer> _serializerOverrides = new();
    private readonly Dictionary<Type, IChangeCompressor> _compressorOverrides = new();
    private readonly Dictionary<Type, IRepository> _repositoryOverrides = new();

    private Func<Type, IOutbox>? _outboxFactory;
    private Func<Type, IQueuePublisher>? _queueFactory;
    private Func<Type, IChangeSerializer>? _serializerFactory;
    private Func<Type, IChangeCompressor>? _compressorFactory;
    private Func<Type, IRepository>? _repositoryFactory;

    private Action<OutboxPublisherOptions>? _optionsConfigure;
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private RayTreeMeter? _meter;
    private bool _built;

    /// <inheritdoc/>
    public IChangePublisherBuilder UseOutbox<T>(Func<Type, IOutbox> factory) where T : IOutbox
    {
        ThrowIfBuilt();
        _outboxFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <inheritdoc/>
    public IChangePublisherBuilder UsePublisher<T>(Func<Type, IQueuePublisher> factory) where T : IQueuePublisher
    {
        ThrowIfBuilt();
        _queueFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <inheritdoc/>
    public IChangePublisherBuilder UseSerializer<T>(Func<Type, IChangeSerializer> factory) where T : IChangeSerializer
    {
        ThrowIfBuilt();
        _serializerFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <inheritdoc/>
    public IChangePublisherBuilder UseCompressor<T>(Func<Type, IChangeCompressor> factory) where T : IChangeCompressor
    {
        ThrowIfBuilt();
        _compressorFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <inheritdoc/>
    public IChangePublisherBuilder UseRepository<T>(Func<Type, IRepository> factory) where T : IRepository
    {
        ThrowIfBuilt();
        _repositoryFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <inheritdoc/>
    public IChangePublisherBuilder UseOptions(Action<OutboxPublisherOptions> configure)
    {
        ThrowIfBuilt();
        _optionsConfigure = configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }

    /// <inheritdoc/>
    public IChangePublisherBuilder UseMeter(RayTreeMeter meter)
    {
        ThrowIfBuilt();
        _meter = meter ?? throw new ArgumentNullException(nameof(meter));
        return this;
    }

    /// <inheritdoc/>
    public IChangePublisherBuilder ForEntity<TEntity>(Action<IEntityPublisherBuilder<TEntity>> configure)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        ThrowIfBuilt();
        var entityBuilder = new EntityPublisherBuilder<TEntity>(this);
        configure(entityBuilder);
        return this;
    }

    /// <inheritdoc/>
    public ChangePublisher Build()
    {
        _built = true;

        var meter = _meter ?? new RayTreeMeter();
        var publisher = new ChangePublisher(_loggerFactory, meter);
        _optionsConfigure?.Invoke(publisher.Options);

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

            var serializer = _serializerOverrides.GetValueOrDefault(entityType) ?? _serializerFactory?.Invoke(entityType)
                ?? throw new InvalidOperationException($"No serializer configured for {entityType.Name}");

            var compressor = _compressorOverrides.GetValueOrDefault(entityType) ?? _compressorFactory?.Invoke(entityType)
                ?? throw new InvalidOperationException($"No compressor configured for {entityType.Name}");

            var repository = _repositoryOverrides.GetValueOrDefault(entityType) ?? _repositoryFactory?.Invoke(entityType);

            publisher.RegisterOutbox(entityType, outbox);
            publisher.RegisterPublisher(entityType, queue);
            publisher.RegisterSerializer(entityType, serializer);
            publisher.RegisterCompressor(entityType, compressor);

            if (repository != null)
                publisher.RegisterRepository(entityType, repository);
        }

        return publisher;
    }

    internal void UseLoggerFactory(ILoggerFactory factory) => _loggerFactory = factory;

    internal void AddOutboxOverride(Type entityType, IOutbox outbox) => _outboxOverrides[entityType] = outbox;
    internal void AddQueueOverride(Type entityType, IQueuePublisher queue) => _queueOverrides[entityType] = queue;
    internal void AddSerializerOverride(Type entityType, IChangeSerializer serializer) => _serializerOverrides[entityType] = serializer;
    internal void AddCompressorOverride(Type entityType, IChangeCompressor compressor) => _compressorOverrides[entityType] = compressor;
    internal void AddRepositoryOverride(Type entityType, IRepository repository) => _repositoryOverrides[entityType] = repository;

    private void ThrowIfBuilt()
    {
        if (_built)
            throw new InvalidOperationException("Configuration has already been built.");
    }
}
