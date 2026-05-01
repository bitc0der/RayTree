using System.IO.Pipelines;
using RayTree.Models;

namespace RayTree.Plugins;

public interface IQueuePublisher
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task PublishAsync(EntityChange change, PipeReader payload, CancellationToken cancellationToken = default);
}
