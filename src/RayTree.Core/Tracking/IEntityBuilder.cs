using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Repository;
using RayTree.Core.Plugins.Serialization;

namespace RayTree.Core.Tracking;

public interface IEntityBuilder
{
    IEntityBuilder UseRepository(IRepository repository);
    IEntityBuilder UseOutbox(IOutbox outbox);
    IEntityBuilder UseQueue(IQueuePublisher queue);
    IEntityBuilder UseSerializer(IChangeSerializer serializer);
    IEntityBuilder UseCompressor(IChangeCompressor compressor);
}
