using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Deduplication;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Telemetry;

namespace RayTree.Core.Handling;

/// <summary>
/// Standalone fluent builder that produces a <see cref="ChangeSubscriber"/> with global
/// defaults and optional per-entity overrides.  Use <see cref="IChangeSubscriberBuilder"/>
/// when the concrete type is not required.
/// </summary>
public sealed class ChangeSubscriberBuilder : IChangeSubscriberBuilder
{
    private IChangeSerializer? _globalSerializer;
    private IChangeCompressor? _globalCompressor;
    private IDeduplicationStore? _dedupStore;
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private RayTreeMeter? _meter;
    private readonly SubscriberOptions _globalOptions = new();
    private readonly List<Action<ChangeSubscriber>> _entityApplicators = new();
    private bool _built;

    /// <inheritdoc/>
    public IChangeSubscriberBuilder UseSerializer(IChangeSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ThrowIfBuilt();
        _globalSerializer = serializer;
        return this;
    }

    /// <inheritdoc/>
    public IChangeSubscriberBuilder UseCompressor(IChangeCompressor compressor)
    {
        ArgumentNullException.ThrowIfNull(compressor);
        ThrowIfBuilt();
        _globalCompressor = compressor;
        return this;
    }

    /// <inheritdoc/>
    public IChangeSubscriberBuilder UseOptions(Action<SubscriberOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ThrowIfBuilt();
        configure(_globalOptions);
        return this;
    }

    /// <inheritdoc/>
    public IChangeSubscriberBuilder UseDeduplicationStore(IDeduplicationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        ThrowIfBuilt();
        _dedupStore = store;
        return this;
    }

    /// <inheritdoc/>
    public IChangeSubscriberBuilder UseMeter(RayTreeMeter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ThrowIfBuilt();
        _meter = meter;
        return this;
    }

    /// <inheritdoc/>
    public IChangeSubscriberBuilder ForEntity<TEntity>(Action<IEntitySubscriberBuilder<TEntity>> configure)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        ThrowIfBuilt();
        var entityBuilder = new EntitySubscriberBuilder<TEntity>(this);
        configure(entityBuilder);
        _entityApplicators.Add(entityBuilder.Apply);
        return this;
    }

    /// <summary>
    /// Builds the subscriber using this builder's configuration.
    /// </summary>
    /// <param name="dedupStoreOverride">
    /// When provided (e.g., resolved from DI), takes precedence over any store set on this
    /// builder via <see cref="UseDeduplicationStore"/> or <see cref="UseRedisDeduplication"/>.
    /// </param>
    /// <param name="optionsOverride">
    /// When provided (e.g., bound from <c>appsettings.json</c> via <c>IOptions</c>), takes
    /// precedence over options configured via <see cref="UseOptions"/>.
    /// </param>
    public ChangeSubscriber Build(
        IDeduplicationStore? dedupStoreOverride = null,
        SubscriberOptions? optionsOverride = null)
    {
        _built = true;
        var effectiveDedupStore = dedupStoreOverride ?? _dedupStore;
        var effectiveOptions    = optionsOverride    ?? _globalOptions;
        var meter               = _meter ?? new RayTreeMeter();
        var logger              = _loggerFactory.CreateLogger<ChangeSubscriber>();
        var subscriber          = new ChangeSubscriber(logger, meter, effectiveDedupStore, effectiveOptions);

        foreach (var apply in _entityApplicators)
            apply(subscriber);

        return subscriber;
    }

    internal void UseLoggerFactory(ILoggerFactory factory) => _loggerFactory = factory;

    /// <summary>Exposes the global serializer to <see cref="EntitySubscriberBuilder{TEntity}"/>.</summary>
    internal IChangeSerializer? GlobalSerializer => _globalSerializer;

    /// <summary>Exposes the global compressor to <see cref="EntitySubscriberBuilder{TEntity}"/>.</summary>
    internal IChangeCompressor? GlobalCompressor => _globalCompressor;

    /// <summary>Exposes the global options to <see cref="EntitySubscriberBuilder{TEntity}"/> for copying.</summary>
    internal SubscriberOptions GlobalOptions => _globalOptions;

    internal void AddEntityApplicator(Action<ChangeSubscriber> applicator)
    {
        ThrowIfBuilt();
        _entityApplicators.Add(applicator);
    }

    // Explicit interface implementation so the IChangeSubscriberBuilder.Build() call
    // uses the parameterless overload without leaking the override parameters on the interface.
    ChangeSubscriber IChangeSubscriberBuilder.Build() => Build();

    private void ThrowIfBuilt()
    {
        if (_built)
            throw new InvalidOperationException("Configuration has already been built.");
    }
}
