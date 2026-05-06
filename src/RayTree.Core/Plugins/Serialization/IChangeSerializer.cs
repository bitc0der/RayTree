using System.IO.Pipelines;
using RayTree.Core.Models;

namespace RayTree.Core.Plugins.Serialization;

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
