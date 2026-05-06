using System.IO.Pipelines;
using MessagePack;
using MessagePack.Resolvers;
using RayTree.Models;
using RayTree.Plugins;

namespace RayTree.Plugins.Serializers.MessagePack;

/// <summary>
/// Serializer plugin that uses MessagePack to serialize and deserialize entity changes.
/// Non-generic operations use <see cref="ContractlessStandardResolver"/> so no attributes are required.
/// Generic operations (with typed <c>State</c>) use the typeless serializer to support arbitrary entity types.
/// </summary>
public class MessagePackSerializerPlugin : IChangeSerializer
{
    public string Name => "MessagePack";

    private static readonly MessagePackSerializerOptions ContractlessOptions =
        MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);

    /// <summary>Serializes a non-generic <see cref="EntityChange"/> to the pipe writer.</summary>
    public async Task SerializeAsync(EntityChange change, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        MessagePackSerializer.Serialize(writer, change, ContractlessOptions, cancellationToken);
        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }

    /// <summary>
    /// Serializes an <see cref="EntityChange{TEntity}"/>, including the typed
    /// <see cref="EntityChange{TEntity}.State"/>, to the pipe writer.
    /// Uses the typeless serializer so no attributes are required on <typeparamref name="TEntity"/>.
    /// </summary>
    public async Task SerializeAsync<TEntity>(EntityChange<TEntity> change, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        MessagePackSerializer.Typeless.Serialize(ms, change);
        await writer.WriteAsync(ms.ToArray(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }

    /// <summary>Deserializes a non-generic <see cref="EntityChange"/> from the pipe reader.</summary>
    public async Task<EntityChange> DeserializeAsync(PipeReader reader, string entityType, CancellationToken cancellationToken = default)
    {
        var result = await reader.ReadAsync(cancellationToken);
        var buffer = result.Buffer;

        try
        {
            var entityChange = MessagePackSerializer.Deserialize<EntityChange>(buffer, ContractlessOptions, cancellationToken);
            return entityChange ?? throw new InvalidOperationException("Deserialized entity change is null");
        }
        finally
        {
            reader.AdvanceTo(buffer.End);
            await reader.CompleteAsync();
        }
    }

    /// <summary>
    /// Deserializes a typed <see cref="EntityChange{TEntity}"/> (restoring the <c>State</c> property)
    /// from the pipe reader. Uses the typeless serializer to match <see cref="SerializeAsync{TEntity}"/>.
    /// </summary>
    public async Task<EntityChange<TEntity>> DeserializeAsync<TEntity>(PipeReader reader, CancellationToken cancellationToken = default)
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

            var entityChange = MessagePackSerializer.Typeless.Deserialize(ms) as EntityChange<TEntity>;
            return entityChange ?? throw new InvalidOperationException("Deserialized entity change is null");
        }
        finally
        {
            reader.AdvanceTo(buffer.End);
            await reader.CompleteAsync();
        }
    }
}
