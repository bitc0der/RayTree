using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using RayTree.Models;
using RayTree.Plugins;

namespace RayTree.Plugins.Serializers.MsgPack;

public class MessagePackSerializerPlugin : IChangeSerializer
{
    public string Name => "MessagePack";

    public async Task SerializeAsync(EntityChange change, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        MessagePackSerializer.Serialize(ms, change);
        var data = ms.ToArray();
        await writer.WriteAsync(data, cancellationToken);
        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }

    public async Task SerializeAsync<TEntity>(EntityChange<TEntity> change, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        MessagePackSerializer.Serialize(ms, change);
        var data = ms.ToArray();
        await writer.WriteAsync(data, cancellationToken);
        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }

    public async Task<EntityChange> DeserializeAsync(PipeReader reader, string entityType, CancellationToken cancellationToken = default)
    {
        var result = await reader.ReadAsync(cancellationToken);
        var buffer = result.Buffer;

        using var ms = new MemoryStream();
        foreach (var segment in buffer)
        {
            await ms.WriteAsync(segment, cancellationToken);
        }
        ms.Position = 0;

        var entityChange = MessagePackSerializer.Deserialize<EntityChange>(ms);
        reader.AdvanceTo(buffer.End);
        return entityChange!;
    }

    public async Task<EntityChange<TEntity>> DeserializeAsync<TEntity>(PipeReader reader, CancellationToken cancellationToken = default)
    {
        var result = await reader.ReadAsync(cancellationToken);
        var buffer = result.Buffer;

        using var ms = new MemoryStream();
        foreach (var segment in buffer)
        {
            await ms.WriteAsync(segment, cancellationToken);
        }
        ms.Position = 0;

        var entityChange = MessagePackSerializer.Deserialize<EntityChange<TEntity>>(ms);
        reader.AdvanceTo(buffer.End);
        return entityChange!;
    }
}
