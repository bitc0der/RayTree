using System.IO.Pipelines;
using ProtoBuf.Meta;
using RayTree.Models;
using RayTree.Plugins;
using RayTree.Tracking;

namespace RayTree.Plugins.Serializers.Protobuf;

public class ProtobufSerializerPlugin : IChangeSerializer
{
    public string Name => "Protobuf";

    private static readonly RuntimeTypeModel Model;

    static ProtobufSerializerPlugin()
    {
        Model = RuntimeTypeModel.Create();
        var entityType = Model.Add(typeof(EntityChange), false);
        entityType.Add(1, nameof(EntityChange.Id));
        entityType.Add(2, nameof(EntityChange.EntityType));
        entityType.Add(3, nameof(EntityChange.EntityId));
        entityType.Add(4, nameof(EntityChange.ChangeType));
        entityType.Add(5, nameof(EntityChange.Timestamp));
        entityType.Add(6, nameof(EntityChange.Version));
        entityType.Add(7, nameof(EntityChange.CorrelationId));
        entityType.Add(8, nameof(EntityChange.Published));

        Model.Add(typeof(ChangeType), false);
    }

    public async Task SerializeAsync(EntityChange change, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        Model.Serialize(writer.AsStream(), change);
        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
    }

    public async Task<EntityChange> DeserializeAsync(PipeReader reader, string entityType, CancellationToken cancellationToken = default)
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

            var entityType = Type.GetType(entityType) ?? typeof(EntityChange);
        var entityChange = (EntityChange)Model.Deserialize(entityType, null, ms);
        reader.AdvanceTo(buffer.End);
        return entityChange ?? throw new InvalidOperationException("Deserialized entity change is null");
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }
}
