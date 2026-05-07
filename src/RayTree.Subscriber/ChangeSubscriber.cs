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

    private async Task ProcessMessageAsync(EntityChange change, byte[] payload, CancellationToken cancellationToken)
    {
        var entityType = ResolveType(change.EntityType);
        if (entityType == null)
            return;

        if (!await _dedupStore.TryMarkProcessedAsync(change.CorrelationId.ToString(), cancellationToken))
            return;

        if (!_handlers.TryGetValue(entityType, out var handlers) || handlers.Count == 0)
            return;

        var matchingHandlers = handlers
            .Where(h => h.ChangeType == null || h.ChangeType == change.ChangeType)
            .ToList();

        foreach (var registration in matchingHandlers)
            await InvokeWithRetryAsync(registration, change, payload, cancellationToken);
    }

    public async Task ConsumeFromConsumerAsync(IQueueConsumer consumer,
        CancellationToken cancellationToken = default)
    {
        await foreach (var (change, payload) in consumer.ConsumeAsync(cancellationToken))
            await ProcessMessageAsync(change, payload, cancellationToken);
    }

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
