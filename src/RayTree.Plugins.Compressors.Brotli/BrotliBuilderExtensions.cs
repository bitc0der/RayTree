using RayTree.Core.Plugins.Compression;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.Compressors.Brotli;

public static class BrotliBuilderExtensions
{
    public static IChangeTrackingBuilder UseBrotliCompressor(this IChangeTrackingBuilder builder)
    {
        return builder == null
            ? throw new ArgumentNullException(nameof(builder))
            : builder.UseCompressor<IChangeCompressor>(_ => new BrotliCompressorPlugin());
    }
}
