using System.IO.Pipelines;
using RayTree.Plugins;

namespace RayTree.Plugins;

public class NoOpCompressorPlugin : IChangeCompressor
{
    public string Name => "NoOp";
    public async Task CompressAsync(PipeReader reader, PipeWriter writer, CancellationToken cancellationToken = default)
    {
        var result = await reader.ReadAsync(cancellationToken);
        var buffer = result.Buffer;

        while (!result.IsCompleted)
        {
            foreach (var segment in buffer)
            {
                await writer.WriteAsync(segment, cancellationToken);
            }

            reader.AdvanceTo(buffer.End);
            result = await reader.ReadAsync(cancellationToken);
            buffer = result.Buffer;
        }

        if (!buffer.IsEmpty)
        {
            foreach (var segment in buffer)
            {
                await writer.WriteAsync(segment, cancellationToken);
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
                await writer.WriteAsync(segment, cancellationToken);
            }

            reader.AdvanceTo(buffer.End);
            result = await reader.ReadAsync(cancellationToken);
            buffer = result.Buffer;
        }

        if (!buffer.IsEmpty)
        {
            foreach (var segment in buffer)
            {
                await writer.WriteAsync(segment, cancellationToken);
            }
            reader.AdvanceTo(buffer.End);
        }

        await writer.FlushAsync(cancellationToken);
        await writer.CompleteAsync();
        await reader.CompleteAsync();
    }
}
