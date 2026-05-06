using System.IO.Pipelines;
using MessagePack;
using RayTree.Models;

namespace RayTree.Plugins.Serializers.MessagePack;

public class MessagePackSerializerPlugin : IChangeSerializer
{
    public string Name => "MessagePack";

    public async Task SerializeAsync<TEntity>(
        EntityChange<TEntity> change,
        PipeWriter writer,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        using var ms = new MemoryStream();
        await MessagePackSerializer.Typeless.SerializeAsync(ms, change, cancellationToken: cancellationToken);
        await writer.WriteAsync(ms.ToArray(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }

    public async Task<EntityChange<TEntity>> DeserializeAsync<TEntity>(
        PipeReader reader,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(reader);

        var result = await reader.ReadAsync(cancellationToken);
        var buffer = result.Buffer;

        try
        {
            using var ms = new MemoryStream();
            foreach (var segment in buffer)
            {
                await ms.WriteAsync(segment, cancellationToken);
            }

            ms.Position = 0;

            var entityChange = await MessagePackSerializer.Typeless.DeserializeAsync(
                stream: ms,
                cancellationToken: cancellationToken) as EntityChange<TEntity>;

            return entityChange ?? throw new InvalidOperationException("Deserialized entity change is null");
        }
        finally
        {
            reader.AdvanceTo(buffer.End);
            await reader.CompleteAsync();
        }
    }
}
