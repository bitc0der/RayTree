using K4os.Compression.LZ4;
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

        // Prefix with the original (uncompressed) length so DecompressAsync can size its
        // output buffer exactly, instead of guessing via a worst-case multiplier that could
        // over-allocate by 255x for a small payload.
        await destination.WriteAsync(BitConverter.GetBytes(input.Length), cancellationToken);
        await destination.WriteAsync(compressed.AsMemory(0, compressedLength), cancellationToken);
    }

    public async Task DecompressAsync(Stream source, Stream destination, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await source.CopyToAsync(ms, cancellationToken);
        var input = ms.ToArray();

        var originalLength = BitConverter.ToInt32(input, 0);
        var decompressed = new byte[originalLength];
        var decompressedLength = LZ4Codec.Decode(input, sizeof(int), input.Length - sizeof(int), decompressed, 0, originalLength);

        await destination.WriteAsync(decompressed.AsMemory(0, decompressedLength), cancellationToken);
    }
}
