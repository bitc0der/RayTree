using System.IO.Pipelines;
using K4os.Compression.LZ4;
using RayTree.Core.Plugins;
using RayTree.Plugins;

namespace RayTree.Plugins.Compressors.Lz4;

public class Lz4CompressorPlugin : IChangeCompressor
{
    public string Name => "LZ4";

    private static int MaxOutputLength(int inputLength) => inputLength + (inputLength / 255) + 16;

    public async Task CompressAsync(PipeReader reader, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        var result = await reader.ReadAsync(cancellationToken);
        var buffer = result.Buffer;

        while (!result.IsCompleted)
        {
            foreach (var segment in buffer)
            {
                var source = segment.ToArray();
                var maxCompressedSize = MaxOutputLength(source.Length);
                var compressed = new byte[maxCompressedSize];
                var compressedLength = LZ4Codec.Encode(source, 0, source.Length, compressed, 0, maxCompressedSize);
                await writer.WriteAsync(compressed.AsMemory(0, compressedLength), cancellationToken);
            }

            reader.AdvanceTo(buffer.End);
            result = await reader.ReadAsync(cancellationToken);
            buffer = result.Buffer;
        }

        if (!buffer.IsEmpty)
        {
            foreach (var segment in buffer)
            {
                var source = segment.ToArray();
                var maxCompressedSize = MaxOutputLength(source.Length);
                var compressed = new byte[maxCompressedSize];
                var compressedLength = LZ4Codec.Encode(source, 0, source.Length, compressed, 0, maxCompressedSize);
                await writer.WriteAsync(compressed.AsMemory(0, compressedLength), cancellationToken);
            }
            reader.AdvanceTo(buffer.End);
        }

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
        await reader.CompleteAsync();
    }

    public async Task DecompressAsync(PipeReader reader, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        var result = await reader.ReadAsync(cancellationToken);
        var buffer = result.Buffer;

        while (!result.IsCompleted)
        {
            foreach (var segment in buffer)
            {
                var source = segment.ToArray();
                var maxDecompressedSize = source.Length * 255;
                var decompressed = new byte[maxDecompressedSize];
                var decompressedLength = LZ4Codec.Decode(source, 0, source.Length, decompressed, 0, maxDecompressedSize);
                await writer.WriteAsync(decompressed.AsMemory(0, decompressedLength), cancellationToken);
            }

            reader.AdvanceTo(buffer.End);
            result = await reader.ReadAsync(cancellationToken);
            buffer = result.Buffer;
        }

        if (!buffer.IsEmpty)
        {
            foreach (var segment in buffer)
            {
                var source = segment.ToArray();
                var maxDecompressedSize = source.Length * 255;
                var decompressed = new byte[maxDecompressedSize];
                var decompressedLength = LZ4Codec.Decode(source, 0, source.Length, decompressed, 0, maxDecompressedSize);
                await writer.WriteAsync(decompressed.AsMemory(0, decompressedLength), cancellationToken);
            }
            reader.AdvanceTo(buffer.End);
        }

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
        await reader.CompleteAsync();
    }
}
