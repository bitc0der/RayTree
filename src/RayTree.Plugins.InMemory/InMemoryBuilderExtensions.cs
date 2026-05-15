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

            builder.ForEntity<TEntity>(e => e.UseOutbox(new InMemoryOutbox()));

            return builder;
        }

        public IChangeTrackingBuilder UseInMemoryOutbox<TEntity>()
            where TEntity : class
        {
            builder.ForEntity<TEntity>(e => e.UseOutbox(new InMemoryOutbox()));

            return builder;
        }

        public IChangeTrackingBuilder UseInMemoryQueue<TEntity>()
            where TEntity : class
        {
            builder.ForEntity<TEntity>(e => e.UsePublisher(new InMemoryQueue()));

            return builder;
        }

        public IChangeTrackingBuilder UseInMemoryAll<TEntity>(
            IChangeSerializer serializer,
            IChangeCompressor compressor)
            where TEntity : class
        {
            ArgumentNullException.ThrowIfNull(serializer);
            ArgumentNullException.ThrowIfNull(compressor);

            builder.ForEntity<TEntity>(e => e
                .UseOutbox(new InMemoryOutbox())
                .UsePublisher(new InMemoryQueue())
                .UseSerializer(serializer)
                .UseCompressor(compressor));

            return builder;
        }
    }
}
