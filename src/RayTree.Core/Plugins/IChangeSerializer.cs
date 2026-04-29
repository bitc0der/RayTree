using RayTree.Models;
using System.IO.Pipelines;

namespace RayTree.Plugins;

public interface IChangeSerializer
{
    string Name { get; }
    Task SerializeAsync(EntityChange change, PipeWriter destination, CancellationToken cancellationToken = default);
    Task<EntityChange> DeserializeAsync(PipeReader source, string entityType, CancellationToken cancellationToken = default);
}
