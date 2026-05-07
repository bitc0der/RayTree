using RayTree.Core.Models;

namespace RayTree.Core.Plugins.Consumer;

public interface IQueueConsumer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<MessageEnvelope> ConsumeAsync(CancellationToken cancellationToken = default);
}
