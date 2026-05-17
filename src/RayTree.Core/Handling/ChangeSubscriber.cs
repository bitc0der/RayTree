using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using RayTree.Core.Models;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Plugins.Deduplication;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Telemetry;
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
    // Shared-mode storage (keyed by entity type)
    private readonly Dictionary<Type, List<HandlerRegistration>> _handlers = new();
    private readonly Dictionary<Type, IChangeSerializer> _serializers = new();
    private readonly Dictionary<Type, IChangeCompressor> _compressors = new();
    private readonly Dictionary<Type, IQueueConsumer> _queues = new();
    private readonly Dictionary<Type, SubscriberOptions> _entityOptions = new();

    // Isolated-mode storage (keyed by EntityHandlerKey)
    // Task 3.1 — consumer per (entity, handlerName)
    private readonly Dictionary<EntityHandlerKey, IQueueConsumer> _isolatedQueues = new();
    // Task 3.2 — handlers per (entity, handlerName)
    private readonly Dictionary<EntityHandlerKey, List<HandlerRegistration>> _isolatedHandlers = new();
    // Per-handler subscriber options (takes precedence over entity-level and global options)
    private readonly Dictionary<EntityHandlerKey, SubscriberOptions> _isolatedOptions = new();

    private readonly IDeduplicationStore _dedupStore;
    private readonly SubscriberOptions _options;
    private readonly ILogger<ChangeSubscriber> _logger;
    private readonly RayTreeMeter _meter;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);
    private DateTime _lastDedupCleanup = DateTime.MinValue;

    // Reflection helper: DeserializeCoreAsync<TEntity>(serializer, stream, ct) → Task<EntityChange>
    private static readonly MethodInfo DeserializeMethod =
        typeof(ChangeSubscriber).GetMethod(
            nameof(DeserializeCoreAsync),
            BindingFlags.NonPublic | BindingFlags.Static)!;

    public ChangeSubscriber(
        ILogger<ChangeSubscriber> logger,
        RayTreeMeter meter,
        IDeduplicationStore? dedupStore = null,
        SubscriberOptions? options = null)
    {
        _logger     = logger ?? throw new ArgumentNullException(nameof(logger));
        _meter      = meter  ?? throw new ArgumentNullException(nameof(meter));
        _dedupStore = dedupStore ?? new InMemoryDeduplicationStore();
        _options    = options    ?? new SubscriberOptions();
    }

    /// <summary>Shared-mode consumers, keyed by entity type.</summary>
    public IReadOnlyDictionary<Type, IQueueConsumer> Queues => _queues;

    /// <summary>
    /// Isolated-mode consumers, keyed by <see cref="EntityHandlerKey"/>.
    /// Exposed for <see cref="RayTree.Hosting.ChangeTrackingHostedService"/> to start one
    /// consume loop per entry. Task 3.6.
    /// </summary>
    public IReadOnlyDictionary<EntityHandlerKey, IQueueConsumer> IsolatedQueues
        => _isolatedQueues;

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

    /// <summary>
    /// Stores the consumer dedicated to a named isolated handler. Called by
    /// <see cref="IsolatedHandlerBuilder{TEntity}.Apply"/> once per unique handler name.
    /// Task 3.4.
    /// </summary>
    internal void RegisterIsolatedConsumer<TEntity>(string handlerName, IQueueConsumer consumer)
        where TEntity : class
    {
        _isolatedQueues[new EntityHandlerKey(typeof(TEntity), handlerName)] = consumer;
    }

    /// <summary>
    /// Registers a typed handler under a specific handler name for Isolated mode.
    /// Handlers sharing a name but targeting different <paramref name="changeType"/> values
    /// live in the same list and are disambiguated by the consume loop. Each handler
    /// binds to exactly one concrete <see cref="ChangeType"/>; register multiple handlers
    /// under the same name to react to multiple change types.
    /// Task 3.3.
    /// </summary>
    internal void RegisterIsolatedHandler<TEntity>(
        string handlerName,
        ChangeType changeType,
        ChangeHandlerAsync<TEntity> handler)
        where TEntity : class
    {
        var key = new EntityHandlerKey(typeof(TEntity), handlerName);
        if (!_isolatedHandlers.TryGetValue(key, out var list))
        {
            list = new List<HandlerRegistration>();
            _isolatedHandlers[key] = list;
        }

        list.Add(new HandlerRegistration
        {
            EntityType = typeof(TEntity),
            ChangeType = changeType,
            Handler    = (change, ct) => handler((EntityChange<TEntity>)change, ct)
        });
    }

    /// <summary>
    /// Registers per-handler <see cref="SubscriberOptions"/> for Isolated mode. These options
    /// take precedence over entity-level and global options when resolving the DOP and retry
    /// configuration for the named handler's consume loop.
    /// </summary>
    internal void RegisterIsolatedOptions<TEntity>(string handlerName, SubscriberOptions options)
        where TEntity : class
    {
        _isolatedOptions[new EntityHandlerKey(typeof(TEntity), handlerName)] = options;
    }

    /// <summary>
    /// Resolves the effective <see cref="SubscriberOptions"/> for a given entity type and
    /// optional handler name. Resolution order (highest to lowest priority):
    /// per-handler isolated options → per-entity options → global options.
    /// </summary>
    private SubscriberOptions GetEffectiveOptions(Type entityType, string? handlerName = null)
    {
        if (handlerName is not null
            && _isolatedOptions.TryGetValue(new EntityHandlerKey(entityType, handlerName), out var isolated))
            return isolated;

        return _entityOptions.GetValueOrDefault(entityType) ?? _options;
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

    public ChangeSubscriber OnChange<TEntity>(ChangeType changeType, ChangeHandlerAsync<TEntity> handler)
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
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism,
            CancellationToken      = cancellationToken
        };
        await Parallel.ForEachAsync(
            consumer.ConsumeAsync(cancellationToken),
            parallelOptions,
            async (envelope, token) => await DispatchAndAcknowledgeAsync(consumer, envelope, token));
    }

    public async Task ConsumeFromQueueAsync<TQueue>(
        TQueue queue,
        Func<TQueue, CancellationToken, IAsyncEnumerable<MessageEnvelope>> reader,
        CancellationToken cancellationToken = default)
    {
        // Note: this overload does not have an IQueueConsumer to acknowledge against.
        // Callers using a custom reader are responsible for any broker acknowledgement
        // outside of ChangeSubscriber. This stays at-most-once by design — the typed
        // overload ConsumeFromConsumerAsync(IQueueConsumer) is the path that participates
        // in the optional Ack/Nack lifecycle.
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism,
            CancellationToken      = cancellationToken
        };
        await Parallel.ForEachAsync(
            reader(queue, cancellationToken),
            parallelOptions,
            async (envelope, token) => await ProcessMessageAsync(envelope, token));
    }

    /// <summary>
    /// Shared-mode dispatch wrapper: invokes <see cref="ProcessMessageAsync"/> and then
    /// calls <see cref="IQueueConsumer.AcknowledgeAsync"/> on success, or
    /// <see cref="IQueueConsumer.NegativeAcknowledgeAsync"/> on failure. For consumers
    /// that don't override the default no-op Ack/Nack methods this is a transparent
    /// passthrough — the at-most-once contract is preserved.
    /// </summary>
    private async Task DispatchAndAcknowledgeAsync(
        IQueueConsumer consumer, MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessMessageAsync(envelope, cancellationToken);
            await consumer.AcknowledgeAsync(envelope, cancellationToken);
        }
        catch
        {
            // NACK first so the broker can requeue / leave the offset alone, then let
            // the exception propagate. The Ack/Nack call is best-effort: a failure here
            // is logged but does not mask the original handler exception.
            try { await consumer.NegativeAcknowledgeAsync(envelope, cancellationToken); }
            catch (Exception nackEx)
            {
                _logger.LogError(nackEx,
                    "NegativeAcknowledgeAsync failed for {EntityType} ({CorrelationId})",
                    envelope.EntityType, envelope.CorrelationId);
            }
            throw;
        }
    }

    /// <summary>
    /// Isolated-mode consume loop: reads envelopes from <paramref name="consumer"/> and
    /// processes each one exclusively through handlers registered under
    /// <paramref name="handlerName"/> for <paramref name="entityType"/>.
    /// Uses dedup key <c>$"{correlationId}:{handlerName}"</c>.
    /// Task 4.2.
    /// </summary>
    public async Task ConsumeIsolatedFromConsumerAsync(
        IQueueConsumer consumer,
        Type entityType,
        string handlerName,
        CancellationToken cancellationToken = default)
    {
        var effectiveOptions = GetEffectiveOptions(entityType, handlerName);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = effectiveOptions.MaxDegreeOfParallelism,
            CancellationToken      = cancellationToken
        };
        await Parallel.ForEachAsync(
            consumer.ConsumeAsync(cancellationToken),
            parallelOptions,
            async (envelope, token) =>
                await DispatchIsolatedAndAcknowledgeAsync(
                    consumer, envelope, entityType, handlerName, effectiveOptions, token));
    }

    /// <summary>
    /// Isolated-mode dispatch wrapper: mirrors <see cref="DispatchAndAcknowledgeAsync"/>
    /// for the per-(entity, handler-name) consume path. On normal completion (handler
    /// success, dedup hit, no-handler skip, SkipOnFailure swallow) calls
    /// <see cref="IQueueConsumer.AcknowledgeAsync"/>; on unhandled exception calls
    /// <see cref="IQueueConsumer.NegativeAcknowledgeAsync"/> before rethrowing.
    /// </summary>
    private async Task DispatchIsolatedAndAcknowledgeAsync(
        IQueueConsumer consumer, MessageEnvelope envelope,
        Type entityType, string handlerName, SubscriberOptions effectiveOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            await ProcessIsolatedMessageAsync(envelope, entityType, handlerName, effectiveOptions, cancellationToken);
            await consumer.AcknowledgeAsync(envelope, cancellationToken);
        }
        catch
        {
            try { await consumer.NegativeAcknowledgeAsync(envelope, cancellationToken); }
            catch (Exception nackEx)
            {
                _logger.LogError(nackEx,
                    "NegativeAcknowledgeAsync failed for isolated handler '{HandlerName}' on {EntityType} ({CorrelationId})",
                    handlerName, entityType.Name, envelope.CorrelationId);
            }
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Message processing
    // -------------------------------------------------------------------------

    public async Task ProcessMessageAsync(MessageEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var changeTag = RayTreeMeter.ChangeTag(envelope.ChangeType);

        var entityType = ResolveType(envelope.EntityType);
        if (entityType == null)
        {
            _meter.SubscriberSkipped.Add(1,
                RayTreeMeter.EntityTag(envelope.EntityType),
                changeTag,
                RayTreeMeter.ReasonTag("unknown_type"));
            _logger.LogWarning("Unknown entity type '{EntityType}' in message envelope, skipping", envelope.EntityType);
            return;
        }

        var entityTag = RayTreeMeter.EntityTag(entityType);

        if (!await _dedupStore.TryMarkProcessedAsync(envelope.CorrelationId.ToString(), cancellationToken))
        {
            _meter.SubscriberDeduplicated.Add(1, entityTag, changeTag);
            _logger.LogDebug("Duplicate message {CorrelationId} for {EntityType}, skipping",
                envelope.CorrelationId, envelope.EntityType);
            return;
        }

        if (!_handlers.TryGetValue(entityType, out var handlers) || handlers.Count == 0)
        {
            _meter.SubscriberSkipped.Add(1, entityTag, changeTag, RayTreeMeter.ReasonTag("no_handler"));
            _logger.LogDebug("No handlers registered for {EntityType}, skipping", entityType.Name);
            return;
        }

        var matchingHandlers = handlers
            .Where(h => h.ChangeType == envelope.ChangeType)
            .ToList();

        if (matchingHandlers.Count == 0)
        {
            _meter.SubscriberSkipped.Add(1, entityTag, changeTag, RayTreeMeter.ReasonTag("no_handler"));
            _logger.LogDebug("No handlers matched change type {ChangeType} for {EntityType}, skipping",
                envelope.ChangeType, entityType.Name);
            return;
        }

        // Deserialize the envelope payload back into a typed EntityChange so handlers
        // receive the full entity state. Falls back to meta-only when no serializer is
        // registered for this entity type.
        var change = await DeserializeEnvelopeAsync(envelope, entityType, cancellationToken);
        var options = GetEffectiveOptions(entityType);

        try
        {
            foreach (var registration in matchingHandlers)
                await InvokeWithRetryAsync(registration, change, options, entityTag, changeTag, cancellationToken);
        }
        catch
        {
            // Revert the dedup mark so the redelivered message can be retried.
            // Only triggered when SkipOnFailure = false and all retries are exhausted.
            _logger.LogWarning(
                "Handler for {EntityType} exhausted all retries on {CorrelationId}; reverting dedup mark so redelivered message can be retried",
                entityType.Name, envelope.CorrelationId);
            await _dedupStore.RevertProcessedAsync(envelope.CorrelationId.ToString(), cancellationToken);
            throw;
        }

        _meter.SubscriberProcessed.Add(1, entityTag, changeTag);
        _meter.SubscriberLagDuration.Record(
            Math.Max(0, (DateTime.UtcNow - envelope.Timestamp).TotalSeconds),
            entityTag, changeTag);

        _logger.LogDebug("Processed {ChangeType} change for {EntityType} ({CorrelationId})",
            envelope.ChangeType, entityType.Name, envelope.CorrelationId);

        await MaybeDedupCleanupAsync(cancellationToken);
    }

    /// <summary>
    /// Isolated-mode message processing. Mirrors <see cref="ProcessMessageAsync"/> but:
    /// <list type="bullet">
    ///   <item>Dedup key is <c>$"{correlationId}:{handlerName}"</c>.</item>
    ///   <item>Only handlers registered under <paramref name="handlerName"/> for
    ///   <paramref name="entityType"/> are dispatched.</item>
    /// </list>
    /// Task 3.5.
    /// </summary>
    private async Task ProcessIsolatedMessageAsync(
        MessageEnvelope envelope,
        Type entityType,
        string handlerName,
        SubscriberOptions effectiveOptions,
        CancellationToken cancellationToken)
    {
        var changeTag = RayTreeMeter.ChangeTag(envelope.ChangeType);
        var entityTag = RayTreeMeter.EntityTag(entityType);

        // Isolated dedup key encodes both message identity and handler name
        var dedupKey = $"{envelope.CorrelationId}:{handlerName}";

        if (!await _dedupStore.TryMarkProcessedAsync(dedupKey, cancellationToken))
        {
            _meter.SubscriberDeduplicated.Add(1, entityTag, changeTag);
            _logger.LogDebug(
                "Duplicate isolated message {CorrelationId} for {EntityType}/{HandlerName}, skipping",
                envelope.CorrelationId, entityType.Name, handlerName);
            return;
        }

        var key = new EntityHandlerKey(entityType, handlerName);
        if (!_isolatedHandlers.TryGetValue(key, out var allHandlers) || allHandlers.Count == 0)
        {
            _meter.SubscriberSkipped.Add(1, entityTag, changeTag, RayTreeMeter.ReasonTag("no_handler"));
            _logger.LogDebug(
                "No isolated handlers registered for {EntityType}/{HandlerName}, skipping",
                entityType.Name, handlerName);
            return;
        }

        var matchingHandlers = allHandlers
            .Where(h => h.ChangeType == envelope.ChangeType)
            .ToList();

        if (matchingHandlers.Count == 0)
        {
            _meter.SubscriberSkipped.Add(1, entityTag, changeTag, RayTreeMeter.ReasonTag("no_handler"));
            _logger.LogDebug(
                "No isolated handlers matched change type {ChangeType} for {EntityType}/{HandlerName}, skipping",
                envelope.ChangeType, entityType.Name, handlerName);
            return;
        }

        var change = await DeserializeEnvelopeAsync(envelope, entityType, cancellationToken);

        try
        {
            foreach (var registration in matchingHandlers)
                await InvokeWithRetryAsync(registration, change, effectiveOptions, entityTag, changeTag, cancellationToken);
        }
        catch
        {
            _logger.LogWarning(
                "Isolated handler '{HandlerName}' for {EntityType} exhausted all retries on {CorrelationId}; reverting dedup mark",
                handlerName, entityType.Name, envelope.CorrelationId);
            await _dedupStore.RevertProcessedAsync(dedupKey, cancellationToken);
            throw;
        }

        _meter.SubscriberProcessed.Add(1, entityTag, changeTag);
        _meter.SubscriberLagDuration.Record(
            Math.Max(0, (DateTime.UtcNow - envelope.Timestamp).TotalSeconds),
            entityTag, changeTag);

        _logger.LogDebug(
            "Isolated handler '{HandlerName}' processed {ChangeType} change for {EntityType} ({CorrelationId})",
            handlerName, envelope.ChangeType, entityType.Name, envelope.CorrelationId);

        await MaybeDedupCleanupAsync(cancellationToken);
    }

    private async Task MaybeDedupCleanupAsync(CancellationToken cancellationToken)
    {
        // Quick check without acquiring the gate — avoid contention on every message.
        if (DateTime.UtcNow - _lastDedupCleanup < _options.DeduplicationCleanupInterval) return;

        // Only one concurrent caller runs cleanup; others skip rather than queue.
        if (!_cleanupGate.Wait(0)) return;
        try
        {
            // Double-check after acquiring so a concurrent caller that already ran doesn't repeat it.
            if (DateTime.UtcNow - _lastDedupCleanup < _options.DeduplicationCleanupInterval) return;

            await _dedupStore.CleanupAsync(_options.DeduplicationRetention, cancellationToken);
            _lastDedupCleanup = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Deduplication store cleanup failed");
        }
        finally
        {
            _cleanupGate.Release();
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
    /// attempts after the initial call. With <c>MaxRetries = N</c> the handler may be
    /// called at most <c>N + 1</c> times total (1 initial + N retries).
    /// The caller is responsible for supplying the effective <paramref name="options"/>
    /// (resolved via <see cref="GetEffectiveOptions"/>).
    /// </summary>
    private async Task InvokeWithRetryAsync(HandlerRegistration registration, EntityChange change,
        SubscriberOptions options,
        KeyValuePair<string, object?> entityTag, KeyValuePair<string, object?> changeTag,
        CancellationToken ct)
    {
        var attempts = 0;
        while (true)
        {
            attempts++;
            var sw = Stopwatch.StartNew();
            try
            {
                await registration.Handler(change, ct);
                sw.Stop();
                _meter.SubscriberProcessingDuration.Record(sw.Elapsed.TotalSeconds, entityTag, changeTag);
                _meter.SubscriberHandlerAttempts.Record(attempts, entityTag);
                return;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _meter.SubscriberProcessingDuration.Record(sw.Elapsed.TotalSeconds, entityTag, changeTag);

                if (attempts > options.MaxRetries)
                {
                    _meter.SubscriberHandlerFailures.Add(1, entityTag, changeTag);
                    // Record the attempts histogram on the failure path too so dashboards
                    // showing retry-shape reflect the worst cases, not just successes.
                    _meter.SubscriberHandlerAttempts.Record(attempts, entityTag);

                    if (options.SkipOnFailure)
                    {
                        _logger.LogError(ex,
                            "Handler for {EntityType} failed after {Attempts} attempt(s), skipping message",
                            registration.EntityType.Name, attempts);
                        return;
                    }

                    throw;
                }

                _logger.LogWarning(ex,
                    "Handler for {EntityType} failed on attempt {Attempt}, retrying",
                    registration.EntityType.Name, attempts);
                await Task.Delay(options.RetryDelay * attempts, ct);
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
        _cleanupGate.Dispose();
    }
}
