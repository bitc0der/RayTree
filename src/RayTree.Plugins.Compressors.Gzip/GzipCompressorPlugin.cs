using System.IO.Compression;
using RayTree.Core.Plugins.Compression;

namespace RayTree.Plugins.Compressors.Gzip;

public class GzipCompressorPlugin : IChangeCompressor
{
    public string Name => "Gzip";

    private readonly CompressionLevel _level;

    public GzipCompressorPlugin(CompressionLevel level = CompressionLevel.Optimal)
    {
        _level = level;
    }

    public async Task CompressAsync(Stream source, Stream destination, CancellationToken cancellationToken = default)
    {
        await using var gzip = new GZipStream(destination, _level, leaveOpen: true);
        await source.CopyToAsync(gzip, cancellationToken);
    }

    public async Task DecompressAsync(Stream source, Stream destination, CancellationToken cancellationToken = default)
    {
        await using var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
        await gzip.CopyToAsync(destination, cancellationToken);
    }
}
