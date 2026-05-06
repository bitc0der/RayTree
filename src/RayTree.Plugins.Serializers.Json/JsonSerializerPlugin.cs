using System.IO.Pipelines;
using System.Text.Json;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Serialization;

namespace RayTree.Plugins.Serializers.Json;

public class JsonSerializerPlugin : IChangeSerializer
{
    public string Name => "Json";

    private static readonly JsonSerializerOptions DefaultOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false
    };

    public async Task SerializeAsync<TEntity>(
        EntityChange<TEntity> change,
        PipeWriter writer,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        await JsonSerializer.SerializeAsync(writer.AsStream(), change, DefaultOptions, cancellationToken);
        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }

    public async Task<EntityChange<TEntity>> DeserializeAsync<TEntity>(
        PipeReader reader,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
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

            var entityChange =
                await JsonSerializer.DeserializeAsync<EntityChange<TEntity>>(ms, DefaultOptions, cancellationToken);
            reader.AdvanceTo(buffer.End);
            return entityChange ?? throw new InvalidOperationException("Deserialized entity change is null");
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }
}
