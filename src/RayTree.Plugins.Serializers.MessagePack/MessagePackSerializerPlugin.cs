using System.IO.Pipelines;
using MessagePack;
using MessagePack.Resolvers;
using RayTree.Models;
using RayTree.Plugins;

namespace RayTree.Plugins.Serializers.MessagePack;

public class MessagePackSerializerPlugin : IChangeSerializer
{
    public string Name => "MessagePack";

    private static readonly MessagePackSerializerOptions DefaultOptions =
        MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);

    public async Task SerializeAsync(EntityChange change, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        MessagePackSerializer.Serialize(writer, change, DefaultOptions, cancellationToken);
        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }

    public async Task<EntityChange> DeserializeAsync(PipeReader reader, string entityType, CancellationToken cancellationToken = default)
    {
        var result = await reader.ReadAsync(cancellationToken);
        var buffer = result.Buffer;

        try
        {
            var entityType = Type.GetType(entityType) ?? typeof(EntityChange);
        var entityChange = MessagePackSerializer.Deserialize(entityType, buffer, DefaultOptions, cancellationToken);
        return (EntityChange)entityChange ?? throw new InvalidOperationException("Deserialized entity change is null");
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }
}
