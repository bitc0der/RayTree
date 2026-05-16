using System.Threading.Channels;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Plugins.Publisher;

namespace RayTree.Plugins.InMemory;

/// <summary>
/// In-memory queue with fan-out delivery. Implements <see cref="IQueuePublisher"/>; each
/// call to <see cref="Subscribe"/> returns a new <see cref="IQueueConsumer"/> backed by a
/// dedicated <see cref="Channel{T}"/> that receives every published message independently.
///
/// <para>Intended for <em>Isolated</em>-mode subscribers in tests and local development.
/// Use it as the factory target:
/// <code>
/// var broadcast = new InMemoryBroadcastQueue();
/// .UseConsumerFactory(name => broadcast.Subscribe())
/// </code>
/// </para>
///
/// <para>A subscriber's channel is completed (and its <see cref="ConsumeAsync"/> ends) when
/// <see cref="Complete"/> is called on the broadcast queue, or when the individual
/// <see cref="InMemoryBroadcastSubscriber"/> is disposed.</para>
/// </summary>
public sealed class InMemoryBroadcastQueue : IQueuePublisher, IDisposable
{
    private readonly Lock _lock = new();
    private readonly List<Channel<MessageEnvelope>> _channels = new();
    private bool _completed;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Returns a new <see cref="IQueueConsumer"/> that receives every message published
    /// after this call. Messages published before <see cref="Subscribe"/> is called are
    /// not delivered to the new subscriber (no replay).
    /// </summary>
    public IQueueConsumer Subscribe()
    {
        var channel = Channel.CreateUnbounded<MessageEnvelope>(
            new UnboundedChannelOptions { SingleReader = true });

        lock (_lock)
        {
            if (_completed)
                channel.Writer.TryComplete();
            else
                _channels.Add(channel);
        }

        return new InMemoryBroadcastSubscriber(channel, this);
    }

    /// <summary>
    /// Publishes <paramref name="envelope"/> to every active subscriber channel.
    /// Channels whose readers have been disposed are silently removed from the internal
    /// channel list so subsequent publishes do not attempt to write to closed channels.
    /// </summary>
    public async Task PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        List<Channel<MessageEnvelope>> snapshot;
        lock (_lock)
            snapshot = new List<Channel<MessageEnvelope>>(_channels);

        List<Channel<MessageEnvelope>>? toRemove = null;

        foreach (var channel in snapshot)
        {
            try
            {
                await channel.Writer.WriteAsync(envelope, cancellationToken);
            }
            catch (ChannelClosedException)
            {
                // Subscriber was disposed — mark for removal.
                toRemove ??= new List<Channel<MessageEnvelope>>();
                toRemove.Add(channel);
            }
        }

        if (toRemove is not null)
        {
            lock (_lock)
                foreach (var ch in toRemove)
                    _channels.Remove(ch);
        }
    }

    /// <summary>
    /// Signals that no more messages will be published. All subscriber channels are
    /// completed; their <see cref="IAsyncEnumerable{T}"/> will drain and then end.
    /// </summary>
    public void Complete()
    {
        lock (_lock)
        {
            _completed = true;
            foreach (var channel in _channels)
                channel.Writer.TryComplete();
            _channels.Clear();
        }
    }

    /// <summary>Removes a subscriber channel when it is disposed.</summary>
    internal void RemoveChannel(Channel<MessageEnvelope> channel)
    {
        lock (_lock)
            _channels.Remove(channel);
    }

    public void Dispose() => Complete();
}

/// <summary>
/// Single-subscriber view of an <see cref="InMemoryBroadcastQueue"/> channel.
/// </summary>
internal sealed class InMemoryBroadcastSubscriber : IQueueConsumer, IDisposable
{
    private readonly Channel<MessageEnvelope> _channel;
    private readonly InMemoryBroadcastQueue _owner;

    internal InMemoryBroadcastSubscriber(
        Channel<MessageEnvelope> channel,
        InMemoryBroadcastQueue owner)
    {
        _channel = channel;
        _owner   = owner;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public IAsyncEnumerable<MessageEnvelope> ConsumeAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _owner.RemoveChannel(_channel);
    }
}
