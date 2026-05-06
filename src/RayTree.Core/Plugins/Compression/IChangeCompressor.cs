namespace RayTree.Core.Plugins;

public interface IChangeCompressor
{
    string Name { get; }
    Task CompressAsync(Stream source, Stream destination, CancellationToken cancellationToken = default);
    Task DecompressAsync(Stream source, Stream destination, CancellationToken cancellationToken = default);
}
