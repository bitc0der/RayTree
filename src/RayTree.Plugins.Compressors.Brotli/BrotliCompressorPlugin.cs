using System.IO.Compression;
using RayTree.Core.Plugins;

namespace RayTree.Plugins.Compressors.Brotli;

public class BrotliCompressorPlugin : IChangeCompressor
{
    public string Name => "Brotli";

    private readonly CompressionLevel _level;

    public BrotliCompressorPlugin(CompressionLevel level = CompressionLevel.Optimal)
    {
        _level = level;
    }

    public async Task CompressAsync(Stream source, Stream destination, CancellationToken cancellationToken = default)
    {
        await using var brotli = new BrotliStream(destination, _level, leaveOpen: true);
        await source.CopyToAsync(brotli, cancellationToken);
    }

    public async Task DecompressAsync(Stream source, Stream destination, CancellationToken cancellationToken = default)
    {
        await using var brotli = new BrotliStream(source, CompressionMode.Decompress, leaveOpen: true);
        await brotli.CopyToAsync(destination, cancellationToken);
    }
}
