using RayTree.Core.Models;

namespace RayTree.Core.Plugins.Publisher;

public interface IQueuePublisher
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default);
}
