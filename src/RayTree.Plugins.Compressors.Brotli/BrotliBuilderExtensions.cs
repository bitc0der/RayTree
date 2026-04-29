using Microsoft.Extensions.DependencyInjection;
using RayTree.Plugins.Compressors.Brotli;

namespace RayTree.Plugins;

public static class BrotliBuilderExtensions
{
    public static IChangeTrackingBuilder UseBrotliCompressor(this IChangeTrackingBuilder builder)
    {
        return builder.UseCompressor<IChangeCompressor>(_ => new BrotliCompressorPlugin());
    }
}
