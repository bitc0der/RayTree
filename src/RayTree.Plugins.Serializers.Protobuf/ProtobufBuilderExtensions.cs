using Microsoft.Extensions.DependencyInjection;
using RayTree.Plugins.Serializers.Protobuf;

namespace RayTree.Plugins;

public static class ProtobufBuilderExtensions
{
    public static IChangeTrackingBuilder UseProtobufSerializer(this IChangeTrackingBuilder builder)
    {
        return builder.UseSerializer<IChangeSerializer>(_ => new ProtobufSerializerPlugin());
    }
}
