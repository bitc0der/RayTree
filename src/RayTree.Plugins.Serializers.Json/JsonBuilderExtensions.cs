using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.Serializers.Json;

public static class JsonBuilderExtensions
{
    public static IChangeTrackingBuilder UseJsonSerializer(this IChangeTrackingBuilder builder)
    {
        return builder == null
            ? throw new ArgumentNullException(nameof(builder))
            : builder.UseSerializer<IChangeSerializer>(_ => new JsonSerializerPlugin());
    }
}
