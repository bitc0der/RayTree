using System.IO.Compression;
using System.IO.Pipelines;
using RayTree.Plugins;

namespace RayTree.Plugins.Compressors.Brotli;

public class BrotliCompressorPlugin : IChangeCompressor
{
    public string Name => "Brotli";

    private readonly CompressionLevel _level;

    public BrotliCompressorPlugin(CompressionLevel level = CompressionLevel.Optimal)
    {
        _level = level;
    }

    public async Task CompressAsync(PipeReader reader, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        await using var brotli = new BrotliStream(writer.AsStream(true), _level, true);
        var result = await reader.ReadAsync(cancellationToken);
        var buffer = result.Buffer;

        while (!result.IsCompleted)
        {
            foreach (var segment in buffer)
            {
                await brotli.WriteAsync(segment, cancellationToken);
            }

            var consumed = buffer.End;
            reader.AdvanceTo(consumed);
            result = await reader.ReadAsync(cancellationToken);
            buffer = result.Buffer;
        }

        if (!buffer.IsEmpty)
        {
            foreach (var segment in buffer)
            {
                await brotli.WriteAsync(segment, cancellationToken);
            }
            reader.AdvanceTo(buffer.End);
        }

        await brotli.FlushAsync(cancellationToken);
        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
        await reader.CompleteAsync();
    }

    public async Task DecompressAsync(PipeReader reader, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        await using var brotli = new BrotliStream(reader.AsStream(true), CompressionMode.Decompress, true);
        var buffer = writer.GetMemory(8192);

        while (true)
        {
            var bytesRead = await brotli.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
                break;

            writer.Advance(bytesRead);
            await writer.FlushAsync(cancellationToken);
            buffer = writer.GetMemory(8192);
        }

        await writer.CompleteAsync();
        await reader.CompleteAsync();
    }
}
