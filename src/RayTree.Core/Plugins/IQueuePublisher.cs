using RayTree.Models;

namespace RayTree.Plugins;

public interface IQueuePublisher
{
    Task PublishAsync(EntityChange change, byte[] payload, CancellationToken cancellationToken = default);
}
