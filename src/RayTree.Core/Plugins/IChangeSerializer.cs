using RayTree.Models;
using System.IO.Pipelines;

namespace RayTree.Plugins;

public interface IChangeSerializer
{
    string Name { get; }

    Task SerializeAsync<TEntity>(
        EntityChange<TEntity> change,
        PipeWriter destination,
        CancellationToken cancellationToken = default)
        where TEntity : class;

    Task<EntityChange<TEntity>> DeserializeAsync<TEntity>(
        PipeReader source,
        CancellationToken cancellationToken = default)
        where TEntity : class;
}
