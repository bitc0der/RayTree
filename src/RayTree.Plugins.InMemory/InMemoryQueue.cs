using System.Threading.Channels;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Plugins.Publisher;

namespace RayTree.Plugins.InMemory;

public class InMemoryQueue : IQueuePublisher, IQueueConsumer, IDisposable
{
    private readonly Channel<MessageEnvelope> _channel =
        Channel.CreateUnbounded<MessageEnvelope>();

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ChannelReader<MessageEnvelope> Reader => _channel.Reader;

    public async Task PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
        => await _channel.Writer.WriteAsync(envelope, cancellationToken);

    // Note: a single consumer per instance is assumed.  The underlying channel uses
    // SingleReader=false so a second concurrent ConsumeAsync call is safe at the
    // channel level, but messages will be split non-deterministically between readers.
    public IAsyncEnumerable<MessageEnvelope> ConsumeAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    public void Complete() => _channel.Writer.TryComplete();

    public void Dispose() => _channel.Writer.TryComplete();
}
