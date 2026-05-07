using RayTree.Plugins.InMemory;

namespace RayTree.Subscriber;

public static class InMemorySubscriberExtensions
{
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
        return builder.UseQueue(queue);
    }
}
