using RayTree.Core.Handling;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Repository;
using RayTree.Core.Plugins.Serialization;

namespace RayTree.Core.Tracking;

public interface IEntityBuilder<TEntity> where TEntity : class
{
    // Publisher side
    IEntityBuilder<TEntity> UseRepository(IRepository repository);
    IEntityBuilder<TEntity> UseOutbox(IOutbox outbox);
    IEntityBuilder<TEntity> UsePublisher(IQueuePublisher queue);
    IEntityBuilder<TEntity> UseSerializer(IChangeSerializer serializer);
    IEntityBuilder<TEntity> UseCompressor(IChangeCompressor compressor);

    // Subscriber side
    IEntityBuilder<TEntity> UseConsumer(IQueueConsumer consumer);
    IEntityBuilder<TEntity> UseSubscriberOptions(Action<SubscriberOptions> configure);
    IEntityBuilder<TEntity> OnInsert(ChangeHandlerAsync<TEntity> handler);
    IEntityBuilder<TEntity> OnUpdate(ChangeHandlerAsync<TEntity> handler);
    IEntityBuilder<TEntity> OnDelete(ChangeHandlerAsync<TEntity> handler);
    IEntityBuilder<TEntity> OnChange(ChangeType? changeType, ChangeHandlerAsync<TEntity> handler);
}
