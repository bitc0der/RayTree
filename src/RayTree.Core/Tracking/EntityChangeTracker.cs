using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Distribution;
using RayTree.Core.Handling;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Telemetry;

namespace RayTree.Core.Tracking;

public sealed class EntityChangeTracker : IEntityChangeTracker
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo> IdProperties = new();

    private readonly ChangePublisher _publisher;
    private readonly ChangeSubscriber? _subscriber;
    private readonly RayTreeMeter _meter;
    private readonly bool _ownsMeter;
    private readonly ILogger<EntityChangeTracker> _log;
    private bool _disposed;

    internal ChangePublisher Publisher => _publisher;
    internal ChangeSubscriber? Subscriber => _subscriber;

    /// <summary>The meter used by this tracker's publisher and subscriber.</summary>
    public RayTreeMeter Meter => _meter;

    public static IChangeTrackingBuilder Create(ILoggerFactory? loggerFactory = null)
        => new ChangeTrackingBuilder(loggerFactory);

    /// <summary>
    /// Constructs a tracker. When <paramref name="ownsMeter"/> is <c>true</c>,
    /// <see cref="Dispose"/> also disposes <paramref name="meter"/>. Builders that create
    /// the meter on the caller's behalf should pass <c>ownsMeter: true</c>; callers that
    /// inject their own meter via <c>UseMeter</c> should pass <c>false</c>.
    /// </summary>
    internal EntityChangeTracker(
        ChangePublisher publisher,
        ChangeSubscriber? subscriber = null,
        RayTreeMeter? meter = null,
        bool ownsMeter = false,
        ILoggerFactory? loggerFactory = null)
    {
        _publisher  = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _subscriber = subscriber;
        _meter      = meter ?? publisher.Meter;
        _ownsMeter  = ownsMeter;
        _log        = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<EntityChangeTracker>();
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_log.IsEnabled(LogLevel.Information))
            _log.LogInformation("ChangeTracking: tracker initialization started");

        try
        {
            await _publisher.InitializeAsync(cancellationToken);

            if (_log.IsEnabled(LogLevel.Debug))
                _log.LogDebug(
                    "ChangeTracking: publisher initialized EntityTypeCount={EntityTypeCount}",
                    _publisher.GetOutboxes().Count);

            if (_subscriber != null)
            {
                await _subscriber.InitializeAsync(cancellationToken);

                if (_log.IsEnabled(LogLevel.Debug))
                    _log.LogDebug(
                        "ChangeTracking: consumers initialized ConsumerCount={ConsumerCount}",
                        _subscriber.ConsumerCount);
            }

            if (_log.IsEnabled(LogLevel.Information))
                _log.LogInformation("ChangeTracking: tracker initialization completed");
        }
        catch
        {
            if (_log.IsEnabled(LogLevel.Warning))
                _log.LogWarning("ChangeTracking: tracker initialization aborted");
            throw;
        }
    }

    internal IOutbox GetOutbox(Type entityType) => _publisher.GetOutbox(entityType);

    public async Task<int> RunCleanupAsync(
        TimeSpan retentionPeriod,
        CancellationToken cancellationToken = default)
    {
        var total = 0;
        foreach (var outbox in _publisher.GetOutboxes().Values)
            total += await outbox.CleanupPublishedAsync(retentionPeriod, cancellationToken);
        return total;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
        => _subscriber?.StartAsync(cancellationToken) ?? Task.CompletedTask;

    public Task StopAsync()
        => _subscriber?.StopAsync() ?? Task.CompletedTask;

    public Task TrackChangeAsync<TEntity>(
        TEntity entity,
        ChangeType changeType,
        CancellationToken cancellationToken = default)
        where TEntity : class
        => TrackTypedAsync(entity, changeType, cancellationToken);

    public Task<EntityChange<TEntity>> TrackInsertAsync<TEntity>(
        TEntity entity,
        CancellationToken cancellationToken = default)
        where TEntity : class
        => TrackTypedAsync(entity, ChangeType.Insert, cancellationToken);

    public Task<EntityChange<TEntity>> TrackUpdateAsync<TEntity>(
        TEntity entity,
        CancellationToken cancellationToken = default)
        where TEntity : class
        => TrackTypedAsync(entity, ChangeType.Update, cancellationToken);

    public Task<EntityChange<TEntity>> TrackDeleteAsync<TEntity>(
        TEntity entity,
        CancellationToken cancellationToken = default)
        where TEntity : class
        => TrackTypedAsync(entity, ChangeType.Delete, cancellationToken);

    public async Task TrackChangeAsync<TEntity>(
        EntityChange<TEntity> change,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(change);

        var outbox = _publisher.GetOutbox(typeof(TEntity));
        await outbox.WriteAsync(change, cancellationToken);

        _meter.OutboxWrites.Add(1,
            RayTreeMeter.EntityTag(typeof(TEntity)),
            RayTreeMeter.ChangeTag(change.ChangeType));
    }

    private async Task<EntityChange<TEntity>> TrackTypedAsync<TEntity>(
        TEntity entity,
        ChangeType changeType,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);

        var change = new EntityChange<TEntity>
        {
            EntityType = typeof(TEntity).FullName!,
            EntityId   = GetEntityId(entity),
            ChangeType = changeType,
            State      = entity
        };

        await TrackChangeAsync(change, cancellationToken);
        return change;
    }

    private static string GetEntityId<TEntity>(TEntity entity)
    {
        var idProperty = IdProperties.GetOrAdd(typeof(TEntity), static t =>
            t.GetProperty("Id") ??
            throw new InvalidOperationException($"Entity {t.Name} must have an Id property"));

        return idProperty.GetValue(entity)?.ToString()
            ?? throw new InvalidOperationException("Id cannot be null");
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
