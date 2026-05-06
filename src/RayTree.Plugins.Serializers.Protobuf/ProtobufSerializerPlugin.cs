using System.Collections.Concurrent;
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

    public Task SerializeAsync<TEntity>(
        EntityChange<TEntity> change,
        Stream destination,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(change);
        var model = GetOrCreateGenericModel<TEntity>();
        model.Serialize(destination, change);
        return Task.CompletedTask;
    }

    public async Task<EntityChange<TEntity>> DeserializeAsync<TEntity>(
        Stream source,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(source);
        var model = GetOrCreateGenericModel<TEntity>();
        // ProtoBuf-net doesn't natively support async; buffer first to avoid blocking
        using var ms = new MemoryStream();
        await source.CopyToAsync(ms, cancellationToken);
        ms.Position = 0;
        var result = model.Deserialize<EntityChange<TEntity>>(ms);
        return result ?? throw new InvalidOperationException("Deserialized entity change is null");
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
