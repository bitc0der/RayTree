using System.IO.Pipelines;
using RayTree.Models;
using RayTree.Plugins;
using RayTree.Tracking;

namespace RayTree.Subscriber;

public delegate Task ChangeHandlerAsync(EntityChange change, byte[] payload, CancellationToken cancellationToken);

public class SubscriberOptions
{
    public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;
    public TimeSpan DeduplicationRetention { get; set; } = TimeSpan.FromHours(24);
    public int MaxRetries { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    public bool SkipOnFailure { get; set; }
}

public class ChangeSubscriber : IDisposable
{
    private readonly Dictionary<Type, List<HandlerRegistration>> _handlers = new();
    private readonly Dictionary<Type, IChangeSerializer> _serializers = new();
    private readonly Dictionary<Type, IChangeCompressor> _compressors = new();
    private readonly IDeduplicationStore _dedupStore;
    private readonly SubscriberOptions _options;
    private readonly CancellationTokenSource _cts = new();

    public ChangeSubscriber(IDeduplicationStore? dedupStore = null, SubscriberOptions? options = null)
    {
        _dedupStore = dedupStore ?? new InMemoryDeduplicationStore();
        _options = options ?? new SubscriberOptions();
    }

    public ChangeSubscriber ForEntity<TEntity>()
    {
        _handlers[typeof(TEntity)] = new List<HandlerRegistration>();
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
        {
            _handlers[typeof(TEntity)] = new List<HandlerRegistration>();
        }

        _handlers[typeof(TEntity)].Add(new HandlerRegistration
        {
            EntityType = typeof(TEntity),
            ChangeType = changeType,
            Handler = handler
        });

        return this;
    }

    public async Task ProcessMessageAsync(EntityChange change, byte[] payload, CancellationToken cancellationToken = default)
    {
        var entityType = Type.GetType(change.EntityType);
        if (entityType == null)
            return;

        if (!await _dedupStore.TryMarkProcessedAsync(change.CorrelationId.ToString(), cancellationToken))
            return;

        if (!_handlers.TryGetValue(entityType, out var handlers) || handlers.Count == 0)
            return;

        var matchingHandlers = handlers.Where(h => h.ChangeType == null || h.ChangeType == change.ChangeType).ToList();

        foreach (var registration in matchingHandlers)
        {
            await InvokeWithRetryAsync(registration, change, payload, cancellationToken);
        }
    }

    private async Task InvokeWithRetryAsync(HandlerRegistration registration, EntityChange change, byte[] payload, CancellationToken ct)
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
                    if (_options.SkipOnFailure)
                        return;
                    throw;
                }

                await Task.Delay(_options.RetryDelay * retries, ct);
            }
        }
    }

    public async Task ConsumeFromQueueAsync<TQueue>(
        TQueue queue,
        Func<TQueue, CancellationToken, IAsyncEnumerable<(EntityChange Change, byte[] Payload)>> reader,
        CancellationToken cancellationToken = default)
    {
        await foreach (var (change, payload) in reader(queue, cancellationToken))
        {
            await ProcessMessageAsync(change, payload, cancellationToken);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

internal class HandlerRegistration
{
    public Type EntityType { get; set; } = null!;
    public ChangeType? ChangeType { get; set; }
    public ChangeHandlerAsync Handler { get; set; } = null!;
}
