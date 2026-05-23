using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Distribution;
using RayTree.Core.Handling;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Plugins.Deduplication;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Repository;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Telemetry;

namespace RayTree.Core.Tracking;

public sealed class ChangeTrackingBuilder : IChangeTrackingBuilder
{
    private readonly ChangePublisherBuilder _publisherBuilder = new();
    private readonly ChangeSubscriberBuilder _subscriberBuilder = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly bool _hasCustomLoggerFactory;
    private readonly ILogger<ChangeTrackingBuilder> _logger;
    private RayTreeMeter? _meter;

    private string? _globalOutboxType;
    private string? _globalPublisherType;
    private string? _globalSerializerType;
    private string? _globalCompressorType;
    private string? _globalRepositoryType;
    private bool _hasCustomDedupStore;
    private readonly List<Type> _entityTypes = new();

    internal ChangeTrackingBuilder(ILoggerFactory? loggerFactory = null)
    {
        _hasCustomLoggerFactory = loggerFactory is not null;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<ChangeTrackingBuilder>();
    }

    public IChangeTrackingBuilder UseOutbox<T>(Func<Type, IOutbox> factory) where T : IOutbox
    {
        _publisherBuilder.UseOutbox<T>(factory);
        _globalOutboxType = typeof(T).Name;
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("ChangeTracking: registered global outbox {Plugin}", typeof(T).Name);
        return this;
    }

    public IChangeTrackingBuilder UsePublisher<T>(Func<Type, IQueuePublisher> factory) where T : IQueuePublisher
    {
        _publisherBuilder.UsePublisher<T>(factory);
        _globalPublisherType = typeof(T).Name;
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("ChangeTracking: registered global publisher {Plugin}", typeof(T).Name);
        return this;
    }

    public IChangeTrackingBuilder UseSerializer<T>(Func<Type, IChangeSerializer> factory) where T : IChangeSerializer
    {
        ArgumentNullException.ThrowIfNull(factory);
        _publisherBuilder.UseSerializer<T>(factory);
        _subscriberBuilder.UseSerializer(factory(typeof(object)));
        _globalSerializerType = typeof(T).Name;
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("ChangeTracking: registered global serializer {Plugin}", typeof(T).Name);
        return this;
    }

    public IChangeTrackingBuilder UseCompressor<T>(Func<Type, IChangeCompressor> factory) where T : IChangeCompressor
    {
        ArgumentNullException.ThrowIfNull(factory);
        _publisherBuilder.UseCompressor<T>(factory);
        _subscriberBuilder.UseCompressor(factory(typeof(object)));
        _globalCompressorType = typeof(T).Name;
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("ChangeTracking: registered global compressor {Plugin}", typeof(T).Name);
        return this;
    }

    public IChangeTrackingBuilder UseRepository<T>(Func<Type, IRepository> factory) where T : IRepository
    {
        _publisherBuilder.UseRepository<T>(factory);
        _globalRepositoryType = typeof(T).Name;
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("ChangeTracking: registered global repository {Plugin}", typeof(T).Name);
        return this;
    }

    public IChangeTrackingBuilder UsePublisherOptions(Action<OutboxPublisherOptions> configure)
    {
        _publisherBuilder.UseOptions(configure);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("ChangeTracking: configured {Plugin}", nameof(OutboxPublisherOptions));
        return this;
    }

    public IChangeTrackingBuilder UseSubscriberOptions(Action<SubscriberOptions> configure)
    {
        _subscriberBuilder.UseOptions(configure);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("ChangeTracking: configured {Plugin}", nameof(SubscriberOptions));
        return this;
    }

    public IChangeTrackingBuilder UseDeduplicationStore(IDeduplicationStore store)
    {
        _subscriberBuilder.UseDeduplicationStore(store);
        _hasCustomDedupStore = true;
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("ChangeTracking: registered deduplication store {Plugin}", store.GetType().Name);
        return this;
    }

    public IChangeTrackingBuilder UseMeter(RayTreeMeter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);
        _meter = meter;
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("ChangeTracking: registered meter {Plugin}", nameof(RayTreeMeter));
        return this;
    }

    public IChangeTrackingBuilder ForEntity<TEntity>(Action<IEntityBuilder<TEntity>> configure)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        _entityTypes.Add(typeof(TEntity));
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("ChangeTracking: configuring entity {EntityType}", typeof(TEntity).Name);

        var entityBuilder = new EntityBuilder<TEntity>(_publisherBuilder, _subscriberBuilder, _loggerFactory);
        configure(entityBuilder);
        entityBuilder.RegisterSubscriberApplicator();
        return this;
    }

    public EntityChangeTracker Build()
    {
        var tracker = BuildInternal();
        try
        {
            tracker.InitializeAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Dispose the partially-initialized tracker (owned meter, background services
            // started by the publisher, dedup store, etc.) — the caller never receives a
            // reference, so without this it would leak.
            tracker.Dispose();
            throw;
        }
        return tracker;
    }

    public async Task<EntityChangeTracker> BuildAsync(CancellationToken cancellationToken = default)
    {
        var tracker = BuildInternal();
        try
        {
            await tracker.InitializeAsync(cancellationToken);
        }
        catch
        {
            tracker.Dispose();
            throw;
        }
        return tracker;
    }

    private EntityChangeTracker BuildInternal()
    {
        var meter = _meter ?? new RayTreeMeter();
        var ownsMeter = _meter == null;  // builder-created meter is disposed by the tracker

        if (ownsMeter && _logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("ChangeTracking: no meter supplied; created default RayTreeMeter (owned by tracker)");

        _publisherBuilder.UseLoggerFactory(_loggerFactory);  // always non-null — resolved once here
        _subscriberBuilder.UseLoggerFactory(_loggerFactory);
        _publisherBuilder.UseMeter(meter);
        _subscriberBuilder.UseMeter(meter);

        var publisher  = _publisherBuilder.Build();
        var subscriber = _subscriberBuilder.Build();

        // Wire the pending-count gauge to the publisher's registered outboxes. The lambda is
        // invoked lazily by the OTel collection callback, by which point InitializeAsync has run.
        meter.RegisterPendingGauge(() =>
            publisher.GetOutboxes().Select(kvp => (kvp.Key, kvp.Value)));

        if (_logger.IsEnabled(LogLevel.Information))
        {
            var entityTypeNames = _entityTypes.Select(t => t.Name).ToArray();
            var plugins = $"Outbox={_globalOutboxType ?? "<none>"} Publisher={_globalPublisherType ?? "<none>"} " +
                          $"Serializer={_globalSerializerType ?? "<none>"} Compressor={_globalCompressorType ?? "<none>"} " +
                          $"Repository={_globalRepositoryType ?? "<none>"}";
            _logger.LogInformation(
                "ChangeTracker built. EntityTypes={EntityTypes} Plugins={Plugins} HasCustomMeter={HasCustomMeter} HasCustomDeduplicationStore={HasCustomDeduplicationStore} HasCustomLoggerFactory={HasCustomLoggerFactory}",
                entityTypeNames,
                plugins,
                _meter is not null,
                _hasCustomDedupStore,
                _hasCustomLoggerFactory);
        }

        return new EntityChangeTracker(publisher, subscriber, meter, ownsMeter: ownsMeter, loggerFactory: _loggerFactory);
    }
}
