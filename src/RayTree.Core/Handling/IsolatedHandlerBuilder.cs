using Microsoft.Extensions.Logging;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Tracking;

namespace RayTree.Core.Handling;

/// <summary>
/// Post-fork builder for <em>Isolated</em> handler-dispatch mode. Returned by
/// <see cref="IEntityBuilder{TEntity}.UseConsumerFactory"/>. Accumulates
/// <c>(handlerName, changeType, handler, options)</c> tuples and validates the registration
/// set at <see cref="Apply"/> time.
/// </summary>
internal sealed class IsolatedHandlerBuilder<TEntity> : IIsolatedHandlerBuilder<TEntity>
    where TEntity : class
{
    private readonly EntitySubscriberBuilder<TEntity> _subBuilder;
    private readonly Func<string, IQueueConsumer> _factory;
    private readonly ILogger<IsolatedHandlerBuilder<TEntity>> _logger;
    private readonly List<(string HandlerName, ChangeType ChangeType, ChangeHandlerAsync<TEntity> Handler, SubscriberOptions? Options)> _entries = new();
    private static readonly string EntityTypeName = typeof(TEntity).Name;

    internal IsolatedHandlerBuilder(
        EntitySubscriberBuilder<TEntity> subBuilder,
        Func<string, IQueueConsumer> factory,
        ILoggerFactory loggerFactory)
    {
        _subBuilder = subBuilder;
        _factory = factory;
        _logger = loggerFactory.CreateLogger<IsolatedHandlerBuilder<TEntity>>();
    }

    /// <inheritdoc/>
    public IIsolatedHandlerBuilder<TEntity> OnInsert(string handlerName, ChangeHandlerAsync<TEntity> handler,
        SubscriberOptions? options = null)
        => OnChange(handlerName, ChangeType.Insert, handler, options);

    /// <inheritdoc/>
    public IIsolatedHandlerBuilder<TEntity> OnUpdate(string handlerName, ChangeHandlerAsync<TEntity> handler,
        SubscriberOptions? options = null)
        => OnChange(handlerName, ChangeType.Update, handler, options);

    /// <inheritdoc/>
    public IIsolatedHandlerBuilder<TEntity> OnDelete(string handlerName, ChangeHandlerAsync<TEntity> handler,
        SubscriberOptions? options = null)
        => OnChange(handlerName, ChangeType.Delete, handler, options);

    /// <inheritdoc/>
    public IIsolatedHandlerBuilder<TEntity> OnChange(string handlerName, ChangeType changeType,
        ChangeHandlerAsync<TEntity> handler, SubscriberOptions? options = null)
    {
        // Task 2.4 — reject null/empty names immediately at registration time
        if (string.IsNullOrEmpty(handlerName))
            throw new ArgumentException(
                "Handler name must be a non-null, non-empty string.", nameof(handlerName));

        ArgumentNullException.ThrowIfNull(handler);
        _entries.Add((handlerName, changeType, handler, options));
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug(
                "ChangeTracking: entity override applied EntityType={EntityType} Override={Override} Plugin={Plugin}",
                EntityTypeName, $"On{changeType}:{handlerName}", HandlerDescriptor.Describe(handler));
        return this;
    }

    /// <summary>
    /// Validates the accumulated registrations and wires all isolated consumers and
    /// handlers into <paramref name="subscriber"/>.
    /// </summary>
    internal void Apply(ChangeSubscriber subscriber)
    {
        // --- Validate: no duplicate (action, handlerName) pairs ---
        var seen = new HashSet<(ChangeType, string)>();
        foreach (var (name, changeType, _, _) in _entries)
        {
            if (!seen.Add((changeType, name)))
            {
                throw new InvalidOperationException(
                    $"Duplicate isolated handler registration: action '{changeType}' with name '{name}' " +
                    $"for entity '{typeof(TEntity).Name}'. Each (action, handlerName) pair must be unique.");
            }
        }

        // --- Invoke factory once per unique name, collect consumers ---
        var distinctNames = _entries.Select(e => e.HandlerName).Distinct(StringComparer.Ordinal).ToList();
        var consumers = new Dictionary<string, IQueueConsumer>(StringComparer.Ordinal);

        foreach (var name in distinctNames)
        {
            var consumer = _factory(name);
            if (consumer is null)
                throw new InvalidOperationException(
                    $"Consumer factory returned null for handler name '{name}' " +
                    $"(entity '{typeof(TEntity).Name}'). " +
                    "Each handler name requires a distinct, non-null IQueueConsumer.");
            consumers[name] = consumer;
        }

        // --- Validate: factory must return distinct instances per name ---
        var instanceToFirstName = new Dictionary<IQueueConsumer, string>(ReferenceEqualityComparer.Instance);
        foreach (var (name, consumer) in consumers)
        {
            if (!instanceToFirstName.TryAdd(consumer, name))
                throw new InvalidOperationException(
                    $"Consumer factory returned the same IQueueConsumer instance for handler names " +
                    $"'{instanceToFirstName[consumer]}' and '{name}' " +
                    $"(entity '{typeof(TEntity).Name}'). " +
                    "Each handler name requires an independent consumer instance with its own ACK lifecycle.");
        }

        // --- Register entity metadata (serializer / compressor / options, no queue) ---
        _subBuilder.ApplyMetadataOnly(subscriber);

        // --- Register per-name consumers ---
        foreach (var (name, consumer) in consumers)
            subscriber.RegisterIsolatedConsumer<TEntity>(name, consumer);

        // --- Collect first non-null options per handler name and register them ---
        var optionsByName = new Dictionary<string, SubscriberOptions>(StringComparer.Ordinal);
        foreach (var (name, _, _, opts) in _entries)
        {
            if (opts is not null)
                optionsByName.TryAdd(name, opts);   // first non-null wins
        }
        foreach (var (name, opts) in optionsByName)
            subscriber.RegisterIsolatedOptions<TEntity>(name, opts);

        // --- Register per-name handlers ---
        foreach (var (name, changeType, handler, _) in _entries)
            subscriber.RegisterIsolatedHandler(name, changeType, handler);
    }
}
