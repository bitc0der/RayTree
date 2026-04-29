namespace RayTree.Plugins;

public static class NoOpBuilderExtensions
{
    public static IChangeTrackingBuilder UseNoOpCompressor(this IChangeTrackingBuilder builder)
    {
        return builder.UseCompressor<IChangeCompressor>(_ => new NoOpCompressorPlugin());
    }
}
