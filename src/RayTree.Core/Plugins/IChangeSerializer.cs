using RayTree.Models;

namespace RayTree.Plugins;

public interface IChangeSerializer
{
    string Name { get; }
    Task SerializeAsync(EntityChange change, Stream destination, CancellationToken cancellationToken = default);
    Task<EntityChange> DeserializeAsync(Stream source, string entityType, CancellationToken cancellationToken = default);
}
