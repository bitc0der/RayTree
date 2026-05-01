using RayTree.Models;

namespace RayTree.Tracking;

public interface IEntityChangeTracker : IDisposable
{
    Task TrackChangeAsync(EntityChange change, CancellationToken cancellationToken = default);
    Task TrackChangeAsync<TEntity>(EntityChange<TEntity> change, CancellationToken cancellationToken = default);
    Task TrackChangesAsync(IEnumerable<EntityChange> changes, CancellationToken cancellationToken = default);
}
