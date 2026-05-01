using RayTree.Models;
using RayTree.Tracking;
using System.IO.Pipelines;

namespace RayTree.Plugins;

public interface IChangeSerializer
{
    string Name { get; }
    Task SerializeAsync(EntityChange change, PipeWriter destination, CancellationToken cancellationToken = default);
    Task SerializeAsync<TEntity>(EntityChange<TEntity> change, PipeWriter destination, CancellationToken cancellationToken = default);
    Task<EntityChange> DeserializeAsync(PipeReader source, string entityType, CancellationToken cancellationToken = default);
    Task<EntityChange<TEntity>> DeserializeAsync<TEntity>(PipeReader source, CancellationToken cancellationToken = default);
}
