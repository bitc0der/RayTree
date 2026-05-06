using RayTree.Core.Models;

namespace RayTree.Core.Plugins.Publisher;

public interface IQueuePublisher
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task PublishAsync(EntityChange change, Stream payload, CancellationToken cancellationToken = default);
}
