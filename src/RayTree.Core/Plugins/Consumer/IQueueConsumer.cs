using RayTree.Core.Models;

namespace RayTree.Core.Plugins.Consumer;

public interface IQueueConsumer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<(EntityChange Change, byte[] Payload)> ConsumeAsync(CancellationToken cancellationToken = default);
}
