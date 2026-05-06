using MessagePack;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Serialization;

namespace RayTree.Plugins.Serializers.MessagePack;

public class MessagePackSerializerPlugin : IChangeSerializer
{
    public string Name => "MessagePack";

    public async Task SerializeAsync<TEntity>(
        EntityChange<TEntity> change,
        Stream destination,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        await MessagePackSerializer.Typeless.SerializeAsync(destination, change, cancellationToken: cancellationToken);
    }

    public async Task<EntityChange<TEntity>> DeserializeAsync<TEntity>(
        Stream source,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var result = await MessagePackSerializer.Typeless.DeserializeAsync(source, cancellationToken: cancellationToken)
            as EntityChange<TEntity>;
        return result ?? throw new InvalidOperationException("Deserialized entity change is null");
    }
}
