using RayTree.Core.Handling;
using RayTree.Core.Models;

namespace RayTree.Plugins.InMemory;

public static class InMemorySubscriberExtensions
{
    public static IAsyncEnumerable<MessageEnvelope> ConsumeFromInMemory(
        this InMemoryQueue queue,
        CancellationToken cancellationToken = default)
    {
        return queue == null ? throw new ArgumentNullException(nameof(queue)) : queue.ConsumeAsync(cancellationToken);
    }

    /// <summary>
    /// Configures an <see cref="InMemoryQueue"/> as the queue source for this entity type.
    /// Pass the same <see cref="InMemoryQueue"/> instance used on the publisher side.
    /// </summary>
    public static IEntitySubscriberBuilder<TEntity> UseInMemoryQueue<TEntity>(
        this IEntitySubscriberBuilder<TEntity> builder,
        InMemoryQueue queue)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(queue);
        return builder.UseConsumer(queue);
    }
}
