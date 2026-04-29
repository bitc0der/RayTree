using RayTree.Models;

namespace RayTree.Plugins;

public interface IChangeSerializer
{
    string Name { get; }
    Task<byte[]> SerializeAsync(EntityChange change, CancellationToken cancellationToken = default);
    Task<EntityChange> DeserializeAsync(byte[] data, string entityType, CancellationToken cancellationToken = default);
}
