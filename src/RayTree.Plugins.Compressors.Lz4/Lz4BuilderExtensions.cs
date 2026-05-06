using Microsoft.Extensions.DependencyInjection;
using RayTree.Core.Plugins;
using RayTree.Core.Tracking;
using RayTree.Plugins.Compressors.Lz4;

namespace RayTree.Plugins;

public static class Lz4BuilderExtensions
{
    public static IChangeTrackingBuilder UseLz4Compressor(this IChangeTrackingBuilder builder)
    {
        return builder.UseCompressor<IChangeCompressor>(_ => new Lz4CompressorPlugin());
    }
}
