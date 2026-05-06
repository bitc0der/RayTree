using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.Serializers.Protobuf;

public static class ProtobufBuilderExtensions
{
    public static IChangeTrackingBuilder UseProtobufSerializer(this IChangeTrackingBuilder builder)
    {
        return builder == null
            ? throw new ArgumentNullException(nameof(builder))
            : builder.UseSerializer<IChangeSerializer>(_ => new ProtobufSerializerPlugin());
    }
}
