using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.InMemory;

public static class InMemoryBuilderExtensions
{
    extension(IChangeTrackingBuilder builder)
    {
        public IChangeTrackingBuilder UseInMemoryRepository<TEntity>()
            where TEntity : class
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.ForEntity<TEntity>().UseOutbox(new InMemoryOutbox());

            return builder;
        }

        public IChangeTrackingBuilder UseInMemoryOutbox<TEntity>()
            where TEntity : class
        {
            builder.ForEntity<TEntity>().UseOutbox(new InMemoryOutbox());

            return builder;
        }

        public IChangeTrackingBuilder UseInMemoryQueue<TEntity>()
            where TEntity : class
        {
            builder.ForEntity<TEntity>().UseQueue(new InMemoryQueue());

            return builder;
        }

        public IChangeTrackingBuilder UseInMemoryAll<TEntity>(
            IChangeSerializer serializer,
            IChangeCompressor compressor)
            where TEntity : class
        {
            ArgumentNullException.ThrowIfNull(serializer);
            ArgumentNullException.ThrowIfNull(compressor);

            var outbox = new InMemoryOutbox();
            var queue = new InMemoryQueue();

            builder.ForEntity<TEntity>()
                .UseOutbox(outbox)
                .UseQueue(queue)
                .UseSerializer(serializer)
                .UseCompressor(compressor);

            return builder;
        }
    }
}
