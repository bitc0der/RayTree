using RayTree.Core.Plugins.Compression;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.Compressors.Gzip;

public static class GzipBuilderExtensions
{
    public static IChangeTrackingBuilder UseGzipCompressor(this IChangeTrackingBuilder builder)
    {
        return builder == null
            ? throw new ArgumentNullException(nameof(builder))
            : builder.UseCompressor<IChangeCompressor>(_ => new GzipCompressorPlugin());
    }
}
