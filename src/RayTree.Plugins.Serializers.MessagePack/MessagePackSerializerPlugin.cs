using MessagePack;
using MessagePack.Resolvers;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Serialization;

namespace RayTree.Plugins.Serializers.MessagePack;

public class MessagePackSerializerPlugin : IChangeSerializer
{
    public string Name => "MessagePack";

    // TEntity is already known statically at both call sites, so there's no need for
    // Typeless mode's embedded-runtime-type-name resolution (reflection-based, paid on
    // every serialize/deserialize call). ContractlessStandardResolver still handles plain
    // POCOs without requiring [MessagePackObject] attributes on user entity types.
    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);

    public async Task SerializeAsync<TEntity>(
        EntityChange<TEntity> change,
        Stream destination,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        await MessagePackSerializer.SerializeAsync(destination, change, Options, cancellationToken);
    }

    public async Task<EntityChange<TEntity>> DeserializeAsync<TEntity>(
        Stream source,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var result = await MessagePackSerializer.DeserializeAsync<EntityChange<TEntity>>(source, Options, cancellationToken);
        return result ?? throw new InvalidOperationException("Deserialized entity change is null");
    }
}
