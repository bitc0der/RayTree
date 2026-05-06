namespace RayTree.Plugins;

public interface IEntityBuilder
{
    IEntityBuilder UseRepository(IRepository repository);
    IEntityBuilder UseOutbox(IOutbox outbox);
    IEntityBuilder UseQueue(IQueuePublisher queue);
    IEntityBuilder UseSerializer(IChangeSerializer serializer);
    IEntityBuilder UseCompressor(IChangeCompressor compressor);
}
