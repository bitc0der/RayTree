using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.Serializers.MessagePack;

public static class MessagePackBuilderExtensions
{
    public static IChangeTrackingBuilder UseMessagePackSerializer(this IChangeTrackingBuilder builder)
    {
        return builder == null
            ? throw new ArgumentNullException(nameof(builder))
            : builder.UseSerializer<IChangeSerializer>(_ => new MessagePackSerializerPlugin());
    }
}
