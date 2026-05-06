using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Reflection;
using ProtoBuf.Meta;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.Serializers.Protobuf;

public class ProtobufSerializerPlugin : IChangeSerializer
{
    public string Name => "Protobuf";

    private static readonly ConcurrentDictionary<Type, RuntimeTypeModel> GenericModels = new();

    public async Task SerializeAsync<TEntity>(
        EntityChange<TEntity> change,
        PipeWriter writer,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(change);

        var model = GetOrCreateGenericModel<TEntity>();
        model.Serialize(writer.AsStream(), change);
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

            var model = GetOrCreateGenericModel<TEntity>();
            var entityChange = model.Deserialize<EntityChange<TEntity>>(ms);
            reader.AdvanceTo(buffer.End);
            return entityChange ?? throw new InvalidOperationException("Deserialized entity change is null");
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    private static RuntimeTypeModel GetOrCreateGenericModel<TEntity>()
        where TEntity : class
    {
        return GenericModels.GetOrAdd(typeof(TEntity), BuildGenericModel<TEntity>);
    }

    private static RuntimeTypeModel BuildGenericModel<TEntity>(Type entityType)
        where TEntity : class
    {
        var model = RuntimeTypeModel.Create();
        model.Add(typeof(ChangeType), false);

        var baseMeta = model.Add(typeof(EntityChange), false);
        RegisterEntityChangeFields(baseMeta);
        baseMeta.AddSubType(9, typeof(EntityChange<TEntity>));

        var genericMeta = model.Add(typeof(EntityChange<TEntity>), false);
        genericMeta.Add(1, nameof(EntityChange<TEntity>.State));

        if (!entityType.IsPrimitive && entityType != typeof(string) && !entityType.IsEnum)
        {
            RegisterPublicProperties(model.Add(entityType, false), entityType);
        }

        return model;
    }

    private static void RegisterEntityChangeFields(MetaType meta)
    {
        meta.Add(1, nameof(EntityChange.Id));
        meta.Add(2, nameof(EntityChange.EntityType));
        meta.Add(3, nameof(EntityChange.EntityId));
        meta.Add(4, nameof(EntityChange.ChangeType));
        meta.Add(5, nameof(EntityChange.Timestamp));
        meta.Add(6, nameof(EntityChange.Version));
        meta.Add(7, nameof(EntityChange.CorrelationId));
        meta.Add(8, nameof(EntityChange.Published));
    }

    private static void RegisterPublicProperties(MetaType meta, Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .OrderBy(p => p.Name)
            .ToArray();

        for (var i = 0; i < properties.Length; i++)
        {
            meta.Add(i + 1, properties[i].Name);
        }
    }
}
