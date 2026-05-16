using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Tracking;

namespace RayTree.Core.Handling;

/// <summary>
/// Internal implementation of <see cref="IEntitySubscriberBuilder{TEntity}"/> that collects
/// per-entity configuration and applies it to a <see cref="ChangeSubscriber"/> on
/// <see cref="Apply"/>.
/// </summary>
internal sealed class EntitySubscriberBuilder<TEntity>(ChangeSubscriberBuilder parent)
    : IEntitySubscriberBuilder<TEntity>
    where TEntity : class
{
    private IQueueConsumer? _queue;
    private IChangeSerializer? _serializer;
    private IChangeCompressor? _compressor;
    private Action<SubscriberOptions>? _optionsConfigure;

    private readonly List<(ChangeType? ChangeType, ChangeHandlerAsync<TEntity> Handler)> _handlers = new();

    /// <inheritdoc/>
    public IEntitySubscriberBuilder<TEntity> UseConsumer(IQueueConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        _queue = consumer;
        return this;
    }

    /// <inheritdoc/>
    public IEntitySubscriberBuilder<TEntity> UseSerializer(IChangeSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        _serializer = serializer;
        return this;
    }

    /// <inheritdoc/>
    public IEntitySubscriberBuilder<TEntity> UseCompressor(IChangeCompressor compressor)
    {
        ArgumentNullException.ThrowIfNull(compressor);
        _compressor = compressor;
        return this;
    }

    /// <inheritdoc/>
    public IEntitySubscriberBuilder<TEntity> UseOptions(Action<SubscriberOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _optionsConfigure = configure;
        return this;
    }

    /// <inheritdoc/>
    public IEntitySubscriberBuilder<TEntity> OnInsert(ChangeHandlerAsync<TEntity> handler)
        => OnChange(ChangeType.Insert, handler);

    /// <inheritdoc/>
    public IEntitySubscriberBuilder<TEntity> OnUpdate(ChangeHandlerAsync<TEntity> handler)
        => OnChange(ChangeType.Update, handler);

    /// <inheritdoc/>
    public IEntitySubscriberBuilder<TEntity> OnDelete(ChangeHandlerAsync<TEntity> handler)
        => OnChange(ChangeType.Delete, handler);

    /// <inheritdoc/>
    public IEntitySubscriberBuilder<TEntity> OnChange(ChangeType? changeType, ChangeHandlerAsync<TEntity> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers.Add((changeType, handler));
        return this;
    }

    /// <summary>
    /// Applies the collected configuration to the <see cref="ChangeSubscriber"/>.
    /// Per-entity values win over global defaults; missing per-entity values fall back
    /// to the parent builder's global values.
    /// </summary>
    internal void Apply(ChangeSubscriber subscriber)
    {
        ApplyMetadataOnly(subscriber);

        foreach (var (changeType, handler) in _handlers)
            subscriber.OnChange(changeType, handler);
    }

    /// <summary>
    /// Registers entity metadata (serializer, compressor, options) with the subscriber
    /// without registering the consumer queue or any handlers. Used by
    /// <see cref="IsolatedHandlerBuilder{TEntity}"/> to set up deserialization context
    /// before registering per-handler consumers and handlers separately.
    /// </summary>
    internal void ApplyMetadataOnly(ChangeSubscriber subscriber)
    {
        var serializer = _serializer ?? parent.GlobalSerializer;
        var compressor = _compressor ?? parent.GlobalCompressor;

        SubscriberOptions? entityOptions = null;
        if (_optionsConfigure is not null)
        {
            entityOptions = new SubscriberOptions
            {
                MaxRetries             = parent.GlobalOptions.MaxRetries,
                RetryDelay             = parent.GlobalOptions.RetryDelay,
                SkipOnFailure          = parent.GlobalOptions.SkipOnFailure,
                MaxDegreeOfParallelism = parent.GlobalOptions.MaxDegreeOfParallelism,
                DeduplicationRetention = parent.GlobalOptions.DeduplicationRetention,
            };
            _optionsConfigure(entityOptions);
        }

        subscriber.RegisterEntity<TEntity>(_queue, serializer, compressor, entityOptions);
    }
}
