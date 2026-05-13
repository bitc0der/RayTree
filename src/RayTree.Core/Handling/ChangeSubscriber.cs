using System.Reflection;
using Microsoft.Extensions.Logging;
using RayTree.Core.Models;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Plugins.Deduplication;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Tracking;

namespace RayTree.Core.Handling;

/// <summary>
/// Handler invoked when an entity change arrives. <paramref name="change"/> carries the fully
/// deserialised entity state in <see cref="EntityChange{TEntity}.State"/>.
/// </summary>
public delegate Task ChangeHandlerAsync<TEntity>(
    EntityChange<TEntity> change,
    CancellationToken cancellationToken)
    where TEntity : class;

public class ChangeSubscriber : IDisposable
{
    private readonly Dictionary<Type, List<HandlerRegistration>> _handlers = new();
    private readonly Dictionary<Type, IChangeSerializer> _serializers = new();
    private readonly Dictionary<Type, IChangeCompressor> _compressors = new();
    private readonly Dictionary<Type, IQueueConsumer> _queues = new();
    private readonly Dictionary<Type, SubscriberOptions> _entityOptions = new();
    private readonly IDeduplicationStore _dedupStore;
    private readonly SubscriberOptions _options;
    private readonly ILogger<ChangeSubscriber> _logger;
    private readonly CancellationTokenSource _cts = new();
    private DateTime _lastDedupCleanup = DateTime.MinValue;

    // Reflection helper: DeserializeCoreAsync<TEntity>(serializer, stream, ct) → Task<EntityChange>
    private static readonly MethodInfo DeserializeMethod =
        typeof(ChangeSubscriber).GetMethod(
            nameof(DeserializeCoreAsync),
            BindingFlags.NonPublic | BindingFlags.Static)!;

    public ChangeSubscriber(
        ILogger<ChangeSubscriber> logger,
        IDeduplicationStore? dedupStore = null,
        SubscriberOptions? options = null)
    {
        _logger     = logger ?? throw new ArgumentNullException(nameof(logger));
        _dedupStore = dedupStore ?? new InMemoryDeduplicationStore();
        _options    = options    ?? new SubscriberOptions();
    }

    public IReadOnlyDictionary<Type, IQueueConsumer> Queues => _queues;

    public ChangeSubscriber ForEntity<TEntity>()
    {
        _handlers[typeof(TEntity)] = new List<HandlerRegistration>();
        return this;
    }

    /// <summary>
    /// Registers a full entity configuration in one call. Used internally by
    /// <see cref="EntitySubscriberBuilder{TEntity}"/> to apply resolved global + per-entity
    /// settings. Any null argument means "use global default / already registered value".
    /// </summary>
    internal void RegisterEntity<TEntity>(
        IQueueConsumer?    queue,
        IChangeSerializer? serializer,
        IChangeCompressor? compressor,
        SubscriberOptions? options)
        where TEntity : class
    {
        _handlers.TryAdd(typeof(TEntity), new List<HandlerRegistration>());
        if (queue      != null) _queues[typeof(TEntity)]       = queue;
        if (serializer != null) _serializers[typeof(TEntity)]  = serializer;
        if (compressor != null) _compressors[typeof(TEntity)]  = compressor;
        if (options    != null) _entityOptions[typeof(TEntity)] = options;
    }

    public ChangeSubscriber RegisterQueue<TEntity>(IQueueConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        _queues[typeof(TEntity)] = consumer;
        return this;
    }

    public ChangeSubscriber UseSerializer<TEntity>(IChangeSerializer serializer)
    {
        _serializers[typeof(TEntity)] = serializer;
        return this;
    }

    public ChangeSubscriber UseCompressor<TEntity>(IChangeCompressor compressor)
    {
        _compressors[typeof(TEntity)] = compressor;
        return this;
    }

    public ChangeSubscriber OnChange<TEntity>(ChangeType? changeType, ChangeHandlerAsync<TEntity> handler)
        where TEntity : class
    {
        if (!_handlers.ContainsKey(typeof(TEntity)))
            _handlers[typeof(TEntity)] = new List<HandlerRegistration>();

        _handlers[typeof(TEntity)].Add(new HandlerRegistration
        {
            EntityType = typeof(TEntity),
            ChangeType = changeType,
            // Wrap the typed handler into a non-generic Func for internal storage.
            // The EntityChange passed here is always EntityChange<TEntity> from deserialization.
            Handler = (change, ct) => handler((EntityChange<TEntity>)change, ct)
        });

        return this;
    }

    // -------------------------------------------------------------------------
    // Core consume loop
    // -------------------------------------------------------------------------

    public async Task ConsumeFromConsumerAsync(IQueueConsumer consumer,
        CancellationToken cancellationToken = default)
    {
        await foreach (var envelope in consumer.ConsumeAsync(cancellationToken))
            await ProcessMessageAsync(envelope, cancellationToken);
    }

    public async Task ConsumeFromQueueAsync<TQueue>(
        TQueue queue,
        Func<TQueue, CancellationToken, IAsyncEnumerable<MessageEnvelope>> reader,
        CancellationToken cancellationToken = default)
    {
        await foreach (var envelope in reader(queue, cancellationToken))
            await ProcessMessageAsync(envelope, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Message processing
    // -------------------------------------------------------------------------

    public async Task ProcessMessageAsync(MessageEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var entityType = ResolveType(envelope.EntityType);
        if (entityType == null)
        {
            _logger.LogWarning("Unknown entity type '{EntityType}' in message envelope, skipping", envelope.EntityType);
            return;
        }

        if (!await _dedupStore.TryMarkProcessedAsync(envelope.CorrelationId.ToString(), cancellationToken))
        {
            _logger.LogDebug("Duplicate message {CorrelationId} for {EntityType}, skipping",
                envelope.CorrelationId, envelope.EntityType);
            return;
        }

        if (!_handlers.TryGetValue(entityType, out var handlers) || handlers.Count == 0)
        {
            _logger.LogDebug("No handlers registered for {EntityType}, skipping", entityType.Name);
            return;
        }

        var matchingHandlers = handlers
            .Where(h => h.ChangeType == null || h.ChangeType == envelope.ChangeType)
            .ToList();

        if (matchingHandlers.Count == 0)
        {
            _logger.LogDebug("No handlers matched change type {ChangeType} for {EntityType}, skipping",
                envelope.ChangeType, entityType.Name);
            return;
        }

        // Deserialize the envelope payload back into a typed EntityChange so handlers
        // receive the full entity state. Falls back to meta-only when no serializer is
        // registered for this entity type.
        var change = await DeserializeEnvelopeAsync(envelope, entityType, cancellationToken);

        try
        {
            foreach (var registration in matchingHandlers)
                await InvokeWithRetryAsync(registration, change, cancellationToken);
        }
        catch
        {
            // Revert the dedup mark so the redelivered message can be retried.
            // Only triggered when SkipOnFailure = false and all retries are exhausted.
            await _dedupStore.RevertProcessedAsync(envelope.CorrelationId.ToString(), cancellationToken);
            throw;
        }

        await MaybeDedupCleanupAsync(cancellationToken);
    }

    private async Task MaybeDedupCleanupAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _lastDedupCleanup < _options.DeduplicationCleanupInterval) return;

        try
        {
            await _dedupStore.CleanupAsync(_options.DeduplicationRetention, cancellationToken);
            _lastDedupCleanup = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Deduplication store cleanup failed");
        }
    }

    // -------------------------------------------------------------------------
    // Deserialization
    // -------------------------------------------------------------------------

    private async Task<EntityChange> DeserializeEnvelopeAsync(
        MessageEnvelope envelope, Type entityType, CancellationToken ct)
    {
        if (!_serializers.TryGetValue(entityType, out var serializer))
        {
            // No serializer registered — create a typed EntityChange<TEntity> with State = null.
            // Using the generic type is required so the handler's (EntityChange<TEntity>) cast succeeds.
            var metaChange = (EntityChange)Activator.CreateInstance(
                typeof(EntityChange<>).MakeGenericType(entityType))!;
            metaChange.EntityType    = envelope.EntityType;
            metaChange.EntityId      = envelope.EntityId;
            metaChange.ChangeType    = envelope.ChangeType;
            metaChange.CorrelationId = envelope.CorrelationId;
            metaChange.Version       = envelope.Version;
            metaChange.Timestamp     = envelope.Timestamp;
            return metaChange;
        }

        using var payloadStream      = new MemoryStream(envelope.Payload);
        using var decompressedStream = new MemoryStream();

        if (_compressors.TryGetValue(entityType, out var compressor))
            await compressor.DecompressAsync(payloadStream, decompressedStream, ct);
        else
            await payloadStream.CopyToAsync(decompressedStream, ct);

        decompressedStream.Position = 0;
        return await InvokeDeserializeAsync(serializer, entityType, decompressedStream, ct);
    }

    /// <summary>
    /// Invokes <see cref="DeserializeCoreAsync{TEntity}"/> via reflection so that the
    /// generic serializer method is called with the correct runtime entity type.
    /// </summary>
    private static Task<EntityChange> InvokeDeserializeAsync(
        IChangeSerializer serializer, Type entityType, Stream source, CancellationToken ct)
        => (Task<EntityChange>)DeserializeMethod
            .MakeGenericMethod(entityType)
            .Invoke(null, [serializer, source, ct])!;

    // Return type is Task<EntityChange> so the cast in InvokeDeserializeAsync works at runtime.
    // EntityChange<TEntity> is implicitly upcasted to EntityChange in the async return.
    private static async Task<EntityChange> DeserializeCoreAsync<TEntity>(
        IChangeSerializer serializer,
        Stream source,
        CancellationToken ct)
        where TEntity : class
        => await serializer.DeserializeAsync<TEntity>(source, ct);

    // -------------------------------------------------------------------------
    // Retry / invocation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Invokes the handler with up to <see cref="SubscriberOptions.MaxRetries"/> retry
    /// attempts after the initial call.  With <c>MaxRetries = N</c> the handler may be
    /// called at most <c>N + 1</c> times total (1 initial + N retries).
    /// Per-entity options registered via <see cref="RegisterEntity{TEntity}"/> take
    /// precedence over the global options supplied to the constructor.
    /// </summary>
    private async Task InvokeWithRetryAsync(HandlerRegistration registration, EntityChange change,
        CancellationToken ct)
    {
        var options = _entityOptions.GetValueOrDefault(registration.EntityType) ?? _options;
        var attempt = 0;
        while (true)
        {
            try
            {
                await registration.Handler(change, ct);
                return;
            }
            catch (Exception ex)
            {
                if (attempt >= options.MaxRetries)
                {
                    if (options.SkipOnFailure)
                    {
                        _logger.LogError(ex,
                            "Handler for {EntityType} failed after {Attempts} attempt(s), skipping message",
                            registration.EntityType.Name, attempt + 1);
                        return;
                    }

                    throw;
                }

                attempt++;
                _logger.LogWarning(ex,
                    "Handler for {EntityType} failed on attempt {Attempt}, retrying",
                    registration.EntityType.Name, attempt);
                await Task.Delay(options.RetryDelay * attempt, ct);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Type? ResolveType(string typeName)
    {
        var t = Type.GetType(typeName);
        if (t != null) return t;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            t = assembly.GetType(typeName);
            if (t != null) return t;
        }

        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
