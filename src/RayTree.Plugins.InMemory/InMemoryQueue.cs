using System.Threading.Channels;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Plugins.Publisher;

namespace RayTree.Plugins.InMemory;

public class InMemoryQueue : IQueuePublisher, IQueueConsumer, IDisposable
{
    private readonly Channel<(EntityChange Change, byte[] Payload)> _channel =
        Channel.CreateUnbounded<(EntityChange, byte[])>();

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ChannelReader<(EntityChange Change, byte[] Payload)> Reader => _channel.Reader;

    public async Task PublishAsync(EntityChange change, Stream payload, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await payload.CopyToAsync(ms, cancellationToken);
        await _channel.Writer.WriteAsync((change, ms.ToArray()), cancellationToken);
    }

    public IAsyncEnumerable<(EntityChange Change, byte[] Payload)> ConsumeAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    public void Complete() => _channel.Writer.Complete();

    public void Dispose() => _channel.Writer.Complete();
}
