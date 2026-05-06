using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Serialization;

namespace RayTree.Core.Tracking;

public interface IChangeTrackingBuilder
{
    IChangeTrackingBuilder UseOutbox<T>(Func<Type, IOutbox> factory) where T : IOutbox;
    IChangeTrackingBuilder UseQueue<T>(Func<Type, IQueuePublisher> factory) where T : IQueuePublisher;
    IChangeTrackingBuilder UseSerializer<T>(Func<Type, IChangeSerializer> factory) where T : IChangeSerializer;
    IChangeTrackingBuilder UseCompressor<T>(Func<Type, IChangeCompressor> factory) where T : IChangeCompressor;
    IEntityBuilder ForEntity<TEntity>();
    EntityChangeTracker Build();
    Task<EntityChangeTracker> BuildAsync(CancellationToken cancellationToken = default);
}
