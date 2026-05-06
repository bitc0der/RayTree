using Microsoft.Extensions.DependencyInjection;
using RayTree.Core.Models;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Tracking;
using RayTree.Plugins.InMemory;

namespace RayTree.Plugins;

public static class InMemoryBuilderExtensions
{
    public static IChangeTrackingBuilder UseInMemoryRepository<TEntity>(this IChangeTrackingBuilder builder)
        where TEntity : class
    {
        _ = builder.ForEntity<TEntity>()
            .UseOutbox(new InMemoryOutbox());
        return builder;
    }

    public static IChangeTrackingBuilder UseInMemoryOutbox<TEntity>(this IChangeTrackingBuilder builder)
        where TEntity : class
    {
        _ = builder.ForEntity<TEntity>()
            .UseOutbox(new InMemoryOutbox());
        return builder;
    }

    public static IChangeTrackingBuilder UseInMemoryQueue<TEntity>(this IChangeTrackingBuilder builder)
        where TEntity : class
    {
        _ = builder.ForEntity<TEntity>()
            .UseQueue(new InMemoryQueue());
        return builder;
    }

    public static IChangeTrackingBuilder UseInMemoryAll<TEntity>(this IChangeTrackingBuilder builder,
        IChangeSerializer serializer, IChangeCompressor compressor)
        where TEntity : class
    {
        var outbox = new InMemoryOutbox();
        var queue = new InMemoryQueue();

        _ = builder.ForEntity<TEntity>()
            .UseOutbox(outbox)
            .UseQueue(queue)
            .UseSerializer(serializer)
            .UseCompressor(compressor);
        return builder;
    }
}

public static class InMemorySubscriberExtensions
{
    public static IAsyncEnumerable<(EntityChange Change, byte[] Payload)> ConsumeFromInMemory(
        this InMemoryQueue queue, CancellationToken cancellationToken = default)
    {
        return queue.ConsumeAsync(cancellationToken);
    }
}
