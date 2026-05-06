using RayTree.Core.Tracking;

namespace RayTree.Core.Plugins.Compression;

public static class NoOpBuilderExtensions
{
    public static IChangeTrackingBuilder UseNoOpCompressor(this IChangeTrackingBuilder builder)
    {
        return builder == null
            ? throw new ArgumentNullException(nameof(builder))
            : builder.UseCompressor<IChangeCompressor>(_ => new NoOpCompressorPlugin());
    }
}
