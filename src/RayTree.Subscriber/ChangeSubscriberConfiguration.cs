using Microsoft.Extensions.DependencyInjection;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Tracking;
using RayTree.Plugins;
using RayTree.Plugins.InMemory;

namespace RayTree.Subscriber;

public class ChangeSubscriberConfiguration
{
    private readonly IServiceCollection _services;
    private readonly ChangeSubscriber _subscriber;
    private bool _built;

    public ChangeSubscriberConfiguration(IServiceCollection services)
    {
        _services = services;
        _subscriber = new ChangeSubscriber();
    }

    public ChangeSubscriberConfiguration ConsumeEntity<T>()
    {
        ThrowIfBuilt();
        _subscriber.ForEntity<T>();
        return this;
    }

    public ChangeSubscriberConfiguration FromKafka<T>(string bootstrapServers, string topic)
    {
        ThrowIfBuilt();
        return this;
    }

    public ChangeSubscriberConfiguration FromRabbitMq<T>(string hostName, string exchangeName, string routingKey)
    {
        ThrowIfBuilt();
        return this;
    }

    public ChangeSubscriberConfiguration FromInMemory<T>(InMemoryQueue queue)
    {
        ThrowIfBuilt();
        _services.AddSingleton(queue);
        return this;
    }

    public ChangeSubscriberConfiguration UseSerializer<T>(IChangeSerializer serializer)
    {
        ThrowIfBuilt();
        _subscriber.UseSerializer<T>(serializer);
        return this;
    }

    public ChangeSubscriberConfiguration UseCompressor<T>(IChangeCompressor compressor)
    {
        ThrowIfBuilt();
        _subscriber.UseCompressor<T>(compressor);
        return this;
    }

    public ChangeSubscriberConfiguration OnChange<T>(ChangeType? changeType, ChangeHandlerAsync handler)
    {
        ThrowIfBuilt();
        _subscriber.OnChange<T>(changeType, handler);
        return this;
    }

    public ChangeSubscriberConfiguration OnInsert<T>(ChangeHandlerAsync handler)
    {
        return OnChange<T>(ChangeType.Insert, handler);
    }

    public ChangeSubscriberConfiguration OnUpdate<T>(ChangeHandlerAsync handler)
    {
        return OnChange<T>(ChangeType.Update, handler);
    }

    public ChangeSubscriberConfiguration OnDelete<T>(ChangeHandlerAsync handler)
    {
        return OnChange<T>(ChangeType.Delete, handler);
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
        return _subscriber;
    }

    private void ThrowIfBuilt()
    {
        if (_built)
            throw new InvalidOperationException("Configuration has already been built.");
    }
}
