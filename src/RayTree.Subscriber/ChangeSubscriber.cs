using System.Reflection;
using RayTree.Core.Models;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Tracking;
using RayTree.Subscriber.Plugins.Deduplication;

namespace RayTree.Subscriber;

public delegate Task ChangeHandlerAsync(EntityChange change, byte[] payload, CancellationToken cancellationToken);

public class ChangeSubscriber : IDisposable
{
    private readonly Dictionary<Type, List<HandlerRegistration>> _handlers = new();
    private readonly Dictionary<Type, IChangeSerializer> _serializers = new();
    private readonly Dictionary<Type, IChangeCompressor> _compressors = new();
    private readonly Dictionary<Type, IQueueConsumer> _queues = new();
    private readonly IDeduplicationStore _dedupStore;
    private readonly SubscriberOptions _options;
    private readonly CancellationTokenSource _cts = new();

    // Reflection helper: DeserializeCoreAsync<TEntity>(serializer, stream, ct) → Task<EntityChange>
    private static readonly MethodInfo DeserializeMethod =
        typeof(ChangeSubscriber).GetMethod(
            nameof(DeserializeCoreAsync),
            BindingFlags.NonPublic | BindingFlags.Static)!;

    public ChangeSubscriber(IDeduplicationStore? dedupStore = null, SubscriberOptions? options = null)
    {
        _dedupStore = dedupStore ?? new InMemoryDeduplicationStore();
        _options    = options   ?? new SubscriberOptions();
    }

    public IReadOnlyDictionary<Type, IQueueConsumer> Queues => _queues;

    public ChangeSubscriber ForEntity<TEntity>()
    {
        _handlers[typeof(TEntity)] = new List<HandlerRegistration>();
        return this;
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

    public ChangeSubscriber OnChange<TEntity>(ChangeType? changeType, ChangeHandlerAsync handler)
    {
        if (!_handlers.ContainsKey(typeof(TEntity)))
            _handlers[typeof(TEntity)] = new List<HandlerRegistration>();

        _handlers[typeof(TEntity)].Add(new HandlerRegistration
        {
            EntityType = typeof(TEntity),
            ChangeType = changeType,
            Handler    = handler
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
            return;

        if (!await _dedupStore.TryMarkProcessedAsync(envelope.CorrelationId.ToString(), cancellationToken))
            return;

        if (!_handlers.TryGetValue(entityType, out var handlers) || handlers.Count == 0)
            return;

        var matchingHandlers = handlers
            .Where(h => h.ChangeType == null || h.ChangeType == envelope.ChangeType)
            .ToList();

        if (matchingHandlers.Count == 0)
            return;

        // Deserialize the envelope payload back into a typed EntityChange so handlers
        // receive the full entity state. Falls back to meta-only when no serializer is
        // registered for this entity type.
        var change = await DeserializeEnvelopeAsync(envelope, entityType, cancellationToken);

        foreach (var registration in matchingHandlers)
            await InvokeWithRetryAsync(registration, change, envelope.Payload, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Deserialization
    // -------------------------------------------------------------------------

    private async Task<EntityChange> DeserializeEnvelopeAsync(
        MessageEnvelope envelope, Type entityType, CancellationToken ct)
    {
        if (!_serializers.TryGetValue(entityType, out var serializer))
        {
            // No serializer registered — return a meta-only EntityChange.
            return new EntityChange
            {
                EntityType    = envelope.EntityType,
                EntityId      = envelope.EntityId,
                ChangeType    = envelope.ChangeType,
                CorrelationId = envelope.CorrelationId,
                Version       = envelope.Version,
                Timestamp     = envelope.Timestamp
            };
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

    private async Task InvokeWithRetryAsync(HandlerRegistration registration, EntityChange change,
        byte[] payload, CancellationToken ct)
    {
        var retries = 0;
        while (retries < _options.MaxRetries)
        {
            try
            {
                await registration.Handler(change, payload, ct);
                return;
            }
            catch (Exception)
            {
                retries++;
                if (retries >= _options.MaxRetries)
                {
                    if (_options.SkipOnFailure) return;
                    throw;
                }

                await Task.Delay(_options.RetryDelay * retries, ct);
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
