using Microsoft.Extensions.DependencyInjection;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Tracking;
using RayTree.Subscriber.Plugins.Deduplication;

namespace RayTree.Subscriber;

public class ChangeSubscriberConfiguration
{
    private readonly IServiceCollection _services;
    private readonly List<Action<ChangeSubscriber>> _configurers = new();
    private bool _built;

    public ChangeSubscriberConfiguration(IServiceCollection services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public ChangeSubscriberConfiguration ConsumeEntity<T>()
    {
        ThrowIfBuilt();
        _configurers.Add(s => s.ForEntity<T>());
        return this;
    }

    public ChangeSubscriberConfiguration UseSerializer<T>(IChangeSerializer serializer)
    {
        ThrowIfBuilt();
        _configurers.Add(s => s.UseSerializer<T>(serializer));
        return this;
    }

    public ChangeSubscriberConfiguration UseCompressor<T>(IChangeCompressor compressor)
    {
        ThrowIfBuilt();
        _configurers.Add(s => s.UseCompressor<T>(compressor));
        return this;
    }

    public ChangeSubscriberConfiguration OnChange<T>(ChangeType? changeType, ChangeHandlerAsync handler)
    {
        ThrowIfBuilt();
        _configurers.Add(s => s.OnChange<T>(changeType, handler));
        return this;
    }

    public ChangeSubscriberConfiguration OnInsert<T>(ChangeHandlerAsync handler)
        => OnChange<T>(ChangeType.Insert, handler);

    public ChangeSubscriberConfiguration OnUpdate<T>(ChangeHandlerAsync handler)
        => OnChange<T>(ChangeType.Update, handler);

    public ChangeSubscriberConfiguration OnDelete<T>(ChangeHandlerAsync handler)
        => OnChange<T>(ChangeType.Delete, handler);

    public ChangeSubscriberConfiguration UseQueue<T>(IQueueConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ThrowIfBuilt();
        _configurers.Add(s => s.RegisterQueue<T>(consumer));
        return this;
    }

    public ChangeSubscriberConfiguration UseDeduplicationStore(IDeduplicationStore store)
    {
        ThrowIfBuilt();
        _services.AddSingleton<IDeduplicationStore>(store);
        return this;
    }

    public ChangeSubscriberConfiguration UseRedisDeduplication(string connectionString)
    {
        _services.AddSingleton<IDeduplicationStore>(new RedisDeduplicationStore(connectionString));
        return this;
    }

    public ChangeSubscriber Build(IDeduplicationStore? dedupStore = null, SubscriberOptions? options = null)
    {
        _built = true;
        var subscriber = new ChangeSubscriber(dedupStore, options);
        foreach (var configure in _configurers)
            configure(subscriber);
        return subscriber;
    }

    private void ThrowIfBuilt()
    {
        if (_built)
            throw new InvalidOperationException("Configuration has already been built.");
    }
}
