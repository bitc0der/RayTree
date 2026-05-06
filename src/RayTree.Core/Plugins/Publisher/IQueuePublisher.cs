using System.IO.Pipelines;
using RayTree.Core.Models;

namespace RayTree.Core.Plugins.Publisher;

public interface IQueuePublisher
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task PublishAsync(EntityChange change, PipeReader payload, CancellationToken cancellationToken = default);
}
