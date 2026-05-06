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

    public Task SerializeAsync<TEntity>(
        EntityChange<TEntity> change,
        Stream destination,
        CancellationToken cancellationToken = default)
        where TEntity : class
        => JsonSerializer.SerializeAsync(destination, change, DefaultOptions, cancellationToken);

    public async Task<EntityChange<TEntity>> DeserializeAsync<TEntity>(
        Stream source,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var result = await JsonSerializer.DeserializeAsync<EntityChange<TEntity>>(source, DefaultOptions, cancellationToken);
        return result ?? throw new InvalidOperationException("Deserialized entity change is null");
    }
}
