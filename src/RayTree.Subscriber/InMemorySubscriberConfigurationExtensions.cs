using RayTree.Plugins.InMemory;

namespace RayTree.Subscriber;

public static class InMemorySubscriberConfigurationExtensions
{
    public static ChangeSubscriberConfiguration UseInMemoryQueue<TEntity>(
        this ChangeSubscriberConfiguration config,
        InMemoryQueue queue)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(queue);
        return config.UseQueue<TEntity>(queue);
    }
}
