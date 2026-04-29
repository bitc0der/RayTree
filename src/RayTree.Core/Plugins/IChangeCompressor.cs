namespace RayTree.Plugins;

public interface IChangeCompressor
{
    string Name { get; }
    Task<byte[]> CompressAsync(byte[] data, CancellationToken cancellationToken = default);
    Task<byte[]> DecompressAsync(byte[] data, CancellationToken cancellationToken = default);
}
