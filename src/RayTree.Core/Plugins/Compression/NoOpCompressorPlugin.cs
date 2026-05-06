namespace RayTree.Core.Plugins.Compression;

public class NoOpCompressorPlugin : IChangeCompressor
{
    public string Name => "NoOp";

    public Task CompressAsync(Stream source, Stream destination, CancellationToken cancellationToken = default)
        => source.CopyToAsync(destination, cancellationToken);

    public Task DecompressAsync(Stream source, Stream destination, CancellationToken cancellationToken = default)
        => source.CopyToAsync(destination, cancellationToken);
}
