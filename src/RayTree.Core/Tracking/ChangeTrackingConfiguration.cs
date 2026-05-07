using RayTree.Core.Distribution;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Repository;
using RayTree.Core.Plugins.Serialization;

namespace RayTree.Core.Tracking;

public class ChangeTrackingConfiguration
{
    private readonly ChangeTrackingBuilder _builder = new();
    private readonly OutboxPublisherOptions _publisherOptions = new();

    private bool _built;

    public ChangeTrackingConfiguration UseOutbox<T>(Func<Type, IOutbox> factory)
        where T : IOutbox
    {
        ThrowIfBuilt();
        _builder.UseOutbox<T>(factory);
        return this;
    }

    public ChangeTrackingConfiguration UseQueue<T>(Func<Type, IQueuePublisher> factory)
        where T : IQueuePublisher
    {
        ThrowIfBuilt();
        _builder.UseQueue<T>(factory);
        return this;
    }

    public ChangeTrackingConfiguration UseSerializer<T>(Func<Type, IChangeSerializer> factory)
        where T : IChangeSerializer
    {
        ThrowIfBuilt();
        _builder.UseSerializer<T>(factory);
        return this;
    }

    public ChangeTrackingConfiguration UseCompressor<T>(Func<Type, IChangeCompressor> factory)
        where T : IChangeCompressor
    {
        ThrowIfBuilt();
        _builder.UseCompressor<T>(factory);
        return this;
    }

    public ChangeTrackingConfiguration UseRepository<T>(Func<Type, IRepository> factory)
        where T : IRepository
    {
        ThrowIfBuilt();
        _builder.UseRepository<T>(factory);
        return this;
    }

    public ChangeTrackingConfiguration ForEntity<TEntity>(Action<IEntityBuilder> configure)
    {
        ThrowIfBuilt();
        _builder.ForEntity<TEntity>(configure);
        return this;
    }

    public ChangeTrackingConfiguration WithPollingInterval(TimeSpan interval)
    {
        ThrowIfBuilt();
        _publisherOptions.PollingInterval = interval;
        return this;
    }

    public ChangeTrackingConfiguration WithBatchSize(int batchSize)
    {
        ThrowIfBuilt();
        _publisherOptions.BatchSize = batchSize;
        return this;
    }

    public EntityChangeTracker Build()
    {
        _built = true;
        return _builder.Build();
    }

    private void ThrowIfBuilt()
    {
        if (_built)
            throw new InvalidOperationException("Configuration has already been built. Create a new instance.");
    }
}
