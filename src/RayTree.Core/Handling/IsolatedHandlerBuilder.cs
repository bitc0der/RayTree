using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Tracking;

namespace RayTree.Core.Handling;

/// <summary>
/// Post-fork builder for <em>Isolated</em> handler-dispatch mode. Returned by
/// <see cref="IEntityBuilder{TEntity}.UseConsumerFactory"/>. Accumulates
/// <c>(handlerName, changeType, handler)</c> tuples and validates the registration set
/// at <see cref="Apply"/> time.
/// </summary>
internal sealed class IsolatedHandlerBuilder<TEntity>(
    EntitySubscriberBuilder<TEntity> subBuilder,
    Func<string, IQueueConsumer> factory)
    : IIsolatedHandlerBuilder<TEntity>
    where TEntity : class
{
    private readonly List<(string HandlerName, ChangeType? ChangeType, ChangeHandlerAsync<TEntity> Handler)> _entries = new();

    /// <inheritdoc/>
    public IIsolatedHandlerBuilder<TEntity> OnInsert(string handlerName, ChangeHandlerAsync<TEntity> handler)
        => OnChange(handlerName, ChangeType.Insert, handler);

    /// <inheritdoc/>
    public IIsolatedHandlerBuilder<TEntity> OnUpdate(string handlerName, ChangeHandlerAsync<TEntity> handler)
        => OnChange(handlerName, ChangeType.Update, handler);

    /// <inheritdoc/>
    public IIsolatedHandlerBuilder<TEntity> OnDelete(string handlerName, ChangeHandlerAsync<TEntity> handler)
        => OnChange(handlerName, ChangeType.Delete, handler);

    /// <inheritdoc/>
    public IIsolatedHandlerBuilder<TEntity> OnChange(string handlerName, ChangeType? changeType, ChangeHandlerAsync<TEntity> handler)
    {
        // Task 2.4 — reject null/empty names immediately at registration time
        if (string.IsNullOrEmpty(handlerName))
            throw new ArgumentException(
                "Handler name must be a non-null, non-empty string.", nameof(handlerName));

        ArgumentNullException.ThrowIfNull(handler);
        _entries.Add((handlerName, changeType, handler));
        return this;
    }

    /// <summary>
    /// Validates the accumulated registrations and wires all isolated consumers and
    /// handlers into <paramref name="subscriber"/>.
    /// </summary>
    internal void Apply(ChangeSubscriber subscriber)
    {
        // --- Validate: no duplicate (action, handlerName) pairs ---
        var seen = new HashSet<(ChangeType?, string)>();
        foreach (var (name, changeType, _) in _entries)
        {
            if (!seen.Add((changeType, name)))
            {
                var actionLabel = changeType?.ToString() ?? "any";
                throw new InvalidOperationException(
                    $"Duplicate isolated handler registration: action '{actionLabel}' with name '{name}' " +
                    $"for entity '{typeof(TEntity).Name}'. Each (action, handlerName) pair must be unique.");
            }
        }

        // --- Invoke factory once per unique name, collect consumers ---
        var distinctNames = _entries.Select(e => e.HandlerName).Distinct(StringComparer.Ordinal).ToList();
        var consumers = new Dictionary<string, IQueueConsumer>(StringComparer.Ordinal);

        foreach (var name in distinctNames)
        {
            var consumer = factory(name);
            if (consumer is null)
                throw new InvalidOperationException(
                    $"Consumer factory returned null for handler name '{name}' " +
                    $"(entity '{typeof(TEntity).Name}'). " +
                    "Each handler name requires a distinct, non-null IQueueConsumer.");
            consumers[name] = consumer;
        }

        // --- Validate: factory must return distinct instances per name ---
        var instancesSeen = new HashSet<IQueueConsumer>(ReferenceEqualityComparer.Instance);
        foreach (var (name, consumer) in consumers)
        {
            if (!instancesSeen.Add(consumer))
                throw new InvalidOperationException(
                    $"Consumer factory returned the same IQueueConsumer instance for multiple handler names " +
                    $"(entity '{typeof(TEntity).Name}'). " +
                    "Each handler name requires an independent consumer instance with its own ACK lifecycle.");
        }

        // --- Register entity metadata (serializer / compressor / options, no queue) ---
        subBuilder.ApplyMetadataOnly(subscriber);

        // --- Register per-name consumers ---
        foreach (var (name, consumer) in consumers)
            subscriber.RegisterIsolatedConsumer<TEntity>(name, consumer);

        // --- Register per-name handlers ---
        foreach (var (name, changeType, handler) in _entries)
            subscriber.RegisterIsolatedHandler(name, changeType, handler);
    }
}
