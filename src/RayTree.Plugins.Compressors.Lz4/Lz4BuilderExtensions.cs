using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.Compressors.Lz4;

public static class Lz4BuilderExtensions
{
    public static IChangeTrackingBuilder UseLz4Compressor(this IChangeTrackingBuilder builder)
    {
        return builder == null
            ? throw new ArgumentNullException(nameof(builder))
            : builder.UseCompressor<IChangeCompressor>(_ => new Lz4CompressorPlugin());
    }
}
