using Microsoft.Extensions.DependencyInjection;
using RayTree.Plugins.Compressors.Gzip;

namespace RayTree.Plugins;

public static class GzipBuilderExtensions
{
    public static IChangeTrackingBuilder UseGzipCompressor(this IChangeTrackingBuilder builder)
    {
        return builder.UseCompressor<IChangeCompressor>(_ => new GzipCompressorPlugin());
    }
}
