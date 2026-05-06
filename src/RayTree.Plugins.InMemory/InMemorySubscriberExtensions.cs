using RayTree.Core.Models;

namespace RayTree.Plugins.InMemory;

public static class InMemorySubscriberExtensions
{
    public static IAsyncEnumerable<(EntityChange Change, byte[] Payload)> ConsumeFromInMemory(
        this InMemoryQueue queue,
        CancellationToken cancellationToken = default)
    {
        return queue == null ? throw new ArgumentNullException(nameof(queue)) : queue.ConsumeAsync(cancellationToken);
    }
}
