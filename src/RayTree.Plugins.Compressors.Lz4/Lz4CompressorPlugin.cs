using K4os.Compression.LZ4;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Compression;

namespace RayTree.Plugins.Compressors.Lz4;

public class Lz4CompressorPlugin : IChangeCompressor
{
    public string Name => "LZ4";

    public async Task CompressAsync(Stream source, Stream destination, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await source.CopyToAsync(ms, cancellationToken);
        var input = ms.ToArray();

        var maxCompressedSize = input.Length + (input.Length / 255) + 16;
        var compressed = new byte[maxCompressedSize];
        var compressedLength = LZ4Codec.Encode(input, 0, input.Length, compressed, 0, maxCompressedSize);

        await destination.WriteAsync(compressed.AsMemory(0, compressedLength), cancellationToken);
    }

    public async Task DecompressAsync(Stream source, Stream destination, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await source.CopyToAsync(ms, cancellationToken);
        var input = ms.ToArray();

        var maxDecompressedSize = input.Length * 255;
        var decompressed = new byte[maxDecompressedSize];
        var decompressedLength = LZ4Codec.Decode(input, 0, input.Length, decompressed, 0, maxDecompressedSize);

        await destination.WriteAsync(decompressed.AsMemory(0, decompressedLength), cancellationToken);
    }
}
