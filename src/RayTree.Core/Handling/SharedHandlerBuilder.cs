using Microsoft.Extensions.Logging;
using RayTree.Core.Tracking;

namespace RayTree.Core.Handling;

/// <summary>
/// Post-fork builder for <em>Shared</em> handler-dispatch mode. Returned by
/// <see cref="IEntityBuilder{TEntity}.UseConsumer"/>. Delegates handler accumulation to
/// the underlying <see cref="EntitySubscriberBuilder{TEntity}"/>, which already holds the
/// bound consumer and resolves global serializer / compressor / options on
/// <see cref="EntitySubscriberBuilder{TEntity}.Apply"/>.
/// </summary>
internal sealed class SharedHandlerBuilder<TEntity> : ISharedHandlerBuilder<TEntity>
    where TEntity : class
{
    private readonly EntitySubscriberBuilder<TEntity> _subBuilder;
    private readonly ILogger _log;
    private static readonly string EntityTypeName = typeof(TEntity).Name;

    internal SharedHandlerBuilder(EntitySubscriberBuilder<TEntity> subBuilder, ILogger log)
    {
        _subBuilder = subBuilder ?? throw new ArgumentNullException(nameof(subBuilder));
        _log = log;
    }

    private void LogHandler(string slot, ChangeHandlerAsync<TEntity> handler)
    {
        if (_log.IsEnabled(LogLevel.Debug))
            _log.LogDebug(
                "ChangeTracking: entity override applied EntityType={EntityType} Override={Override} Plugin={Plugin}",
                EntityTypeName, slot, handler.Method.DeclaringType?.Name ?? "<delegate>");
    }

    /// <inheritdoc/>
    public ISharedHandlerBuilder<TEntity> OnInsert(ChangeHandlerAsync<TEntity> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _subBuilder.OnInsert(handler);
        LogHandler("OnInsert", handler);
        return this;
    }

    /// <inheritdoc/>
    public ISharedHandlerBuilder<TEntity> OnUpdate(ChangeHandlerAsync<TEntity> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _subBuilder.OnUpdate(handler);
        LogHandler("OnUpdate", handler);
        return this;
    }

    /// <inheritdoc/>
    public ISharedHandlerBuilder<TEntity> OnDelete(ChangeHandlerAsync<TEntity> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _subBuilder.OnDelete(handler);
        LogHandler("OnDelete", handler);
        return this;
    }

    /// <inheritdoc/>
    public ISharedHandlerBuilder<TEntity> OnChange(ChangeType changeType, ChangeHandlerAsync<TEntity> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _subBuilder.OnChange(changeType, handler);
        LogHandler($"OnChange:{changeType}", handler);
        return this;
    }
}
