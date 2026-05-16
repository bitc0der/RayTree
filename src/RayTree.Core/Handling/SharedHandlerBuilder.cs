using RayTree.Core.Tracking;

namespace RayTree.Core.Handling;

/// <summary>
/// Post-fork builder for <em>Shared</em> handler-dispatch mode. Returned by
/// <see cref="IEntityBuilder{TEntity}.UseConsumer"/>. Delegates handler accumulation to
/// the underlying <see cref="EntitySubscriberBuilder{TEntity}"/>, which already holds the
/// bound consumer and resolves global serializer / compressor / options on
/// <see cref="EntitySubscriberBuilder{TEntity}.Apply"/>.
/// </summary>
internal sealed class SharedHandlerBuilder<TEntity>(EntitySubscriberBuilder<TEntity> subBuilder)
    : ISharedHandlerBuilder<TEntity>
    where TEntity : class
{
    /// <inheritdoc/>
    public ISharedHandlerBuilder<TEntity> OnInsert(ChangeHandlerAsync<TEntity> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        subBuilder.OnInsert(handler);
        return this;
    }

    /// <inheritdoc/>
    public ISharedHandlerBuilder<TEntity> OnUpdate(ChangeHandlerAsync<TEntity> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        subBuilder.OnUpdate(handler);
        return this;
    }

    /// <inheritdoc/>
    public ISharedHandlerBuilder<TEntity> OnDelete(ChangeHandlerAsync<TEntity> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        subBuilder.OnDelete(handler);
        return this;
    }

    /// <inheritdoc/>
    public ISharedHandlerBuilder<TEntity> OnChange(ChangeType? changeType, ChangeHandlerAsync<TEntity> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        subBuilder.OnChange(changeType, handler);
        return this;
    }
}
