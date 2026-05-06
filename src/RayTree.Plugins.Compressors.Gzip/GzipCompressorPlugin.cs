using System.IO.Compression;
using System.IO.Pipelines;
using RayTree.Core.Plugins;

namespace RayTree.Plugins.Compressors.Gzip;

public class GzipCompressorPlugin : IChangeCompressor
{
    public string Name => "Gzip";

    private readonly CompressionLevel _level;

    public GzipCompressorPlugin(CompressionLevel level = CompressionLevel.Optimal)
    {
        _level = level;
    }

    public async Task CompressAsync(PipeReader reader, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await using (var gzip = new GZipStream(ms, _level, true))
        {
            var result = await reader.ReadAsync(cancellationToken);
            var buffer = result.Buffer;

            while (!result.IsCompleted)
            {
                foreach (var segment in buffer)
                {
                    await gzip.WriteAsync(segment, cancellationToken);
                }

                reader.AdvanceTo(buffer.End);
                result = await reader.ReadAsync(cancellationToken);
                buffer = result.Buffer;
            }

            if (!buffer.IsEmpty)
            {
                foreach (var segment in buffer)
                {
                    await gzip.WriteAsync(segment, cancellationToken);
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

    public async Task DecompressAsync(PipeReader reader, PipeWriter writer,
        CancellationToken cancellationToken = default)
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
        await using var gzip = new GZipStream(ms, CompressionMode.Decompress, true);
        await gzip.CopyToAsync(writer.AsStream(), cancellationToken);

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
        await reader.CompleteAsync();
    }
}
