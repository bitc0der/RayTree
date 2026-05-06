namespace RayTree.Tracking;

public interface IEntityChangeTracker : IDisposable
{
    Task TrackChangeAsync<TEntity>(TEntity entity, ChangeType changeType, CancellationToken cancellationToken = default)
        where TEntity : class;
}
