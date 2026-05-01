using System.IO.Pipelines;
using System.Threading.Channels;
using RayTree.Models;
using RayTree.Plugins;

namespace RayTree.Plugins.InMemory;

public class InMemoryQueue : IQueuePublisher, IDisposable
{
    private readonly Channel<(EntityChange Change, byte[] Payload)> _channel =
        Channel.CreateUnbounded<(EntityChange, byte[])>();

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // No initialization needed for in-memory queue
        return Task.CompletedTask;
    }

    public ChannelReader<(EntityChange Change, byte[] Payload)> Reader => _channel.Reader;

    public async Task PublishAsync(EntityChange change, PipeReader payload, CancellationToken cancellationToken = default)
    {
        var data = await ReadPipeAsync(payload, cancellationToken);
        await _channel.Writer.WriteAsync((change, data), cancellationToken);
    }

    public IAsyncEnumerable<(EntityChange Change, byte[] Payload)> ConsumeAsync(CancellationToken cancellationToken = default)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }

    public void Complete() => _channel.Writer.Complete();

    private static async Task<byte[]> ReadPipeAsync(PipeReader reader, CancellationToken cancellationToken)
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

        await reader.CompleteAsync();
        return ms.ToArray();
    }

    public void Dispose() => _channel.Writer.Complete();
}
