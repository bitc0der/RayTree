using Microsoft.Extensions.DependencyInjection;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Tracking;
using RayTree.Plugins.Serializers.MessagePack;

namespace RayTree.Plugins;

public static class MessagePackBuilderExtensions
{
    public static IChangeTrackingBuilder UseMessagePackSerializer(this IChangeTrackingBuilder builder)
    {
        return builder.UseSerializer<IChangeSerializer>(_ => new MessagePackSerializerPlugin());
    }
}
