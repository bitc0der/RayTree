using Microsoft.Extensions.DependencyInjection;
using RayTree.Plugins.Serializers.Json;

namespace RayTree.Plugins;

public static class JsonBuilderExtensions
{
    public static IChangeTrackingBuilder UseJsonSerializer(this IChangeTrackingBuilder builder)
    {
        return builder.UseSerializer<IChangeSerializer>(_ => new JsonSerializerPlugin());
    }
}
