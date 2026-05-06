using System.IO.Compression;
using System.IO.Pipelines;
using RayTree.Core.Plugins;
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
        using var ms = new MemoryStream();
        await using (var brotli = new BrotliStream(ms, _level, true))
        {
            var result = await reader.ReadAsync(cancellationToken);
            var buffer = result.Buffer;

            while (!result.IsCompleted)
            {
                foreach (var segment in buffer)
                {
                    await brotli.WriteAsync(segment, cancellationToken);
                }
                reader.AdvanceTo(buffer.End);
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
        }

        ms.Position = 0;
        await ms.CopyToAsync(writer.AsStream(), cancellationToken);

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
        await reader.CompleteAsync();
    }

    public async Task DecompressAsync(PipeReader reader, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        var result = await reader.ReadAsync(cancellationToken);
        var buffer = result.Buffer;

        while (!result.IsCompleted)
        {
            foreach (var segment in buffer)
            {
                await ms.WriteAsync(segment, cancellationToken);
            }
            reader.AdvanceTo(buffer.End);
            result = await reader.ReadAsync(cancellationToken);
            buffer = result.Buffer;
        }

        if (!buffer.IsEmpty)
        {
            foreach (var segment in buffer)
            {
                await ms.WriteAsync(segment, cancellationToken);
            }
            reader.AdvanceTo(buffer.End);
        }

        ms.Position = 0;
        await using var brotli = new BrotliStream(ms, CompressionMode.Decompress, true);
        await brotli.CopyToAsync(writer.AsStream(), cancellationToken);

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
        await reader.CompleteAsync();
    }
}
