using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Confluent.Kafka;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.Kafka;

public class KafkaConsumer : IQueueConsumer, IDisposable
{
    private readonly KafkaConsumerOptions _options;
    private readonly CancellationTokenSource _disposeCts = new();
    private IConsumer<string, byte[]>? _consumer;
    private Task? _pollTask;
    private volatile bool _assigned;

    /// <summary>
    /// Returns <see langword="true"/> once the poll loop has made at least one successful
    /// call to <c>Consume()</c>, which indicates that the Kafka broker has acknowledged the
    /// subscription and partition assignment is underway.  Tests can poll this property
    /// instead of using a fixed <see cref="Task.Delay"/> before publishing.
    /// </summary>
    public bool IsAssigned => _assigned;

    public KafkaConsumer(KafkaConsumerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId          = _options.GroupId,
            AutoOffsetReset  = _options.FromEarliest ? AutoOffsetReset.Earliest : AutoOffsetReset.Latest,
            EnableAutoCommit = false
        };

        _consumer = new ConsumerBuilder<string, byte[]>(config).Build();
        _consumer.Subscribe(_options.Topic);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<MessageEnvelope> ConsumeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_consumer == null)
            throw new InvalidOperationException(
                $"{nameof(InitializeAsync)} must be called before {nameof(ConsumeAsync)}.");

        // All Confluent.Kafka operations (Consume + Commit) must run on the same thread.
        // A dedicated background thread polls and buffers envelopes via an unbounded channel.
        // Linking with _disposeCts ensures Dispose() can drain the poll loop before freeing
        // native memory, preventing AccessViolationException.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var linkedToken = linkedCts.Token;

        var channel = Channel.CreateUnbounded<MessageEnvelope>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        _pollTask = Task.Run(() =>
        {
            var timeout = TimeSpan.FromMilliseconds(_options.PollTimeoutMs);
            try
            {
                while (!linkedToken.IsCancellationRequested)
                {
                    ConsumeResult<string, byte[]>? result;
                    try
                    {
                        result = _consumer!.Consume(timeout);
                        // First successful poll — subscription is active.
                        if (!_assigned) _assigned = true;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException)    { break; }
                    catch (KafkaException ex) when (ex.Error.IsFatal)
                    {
                        // Fatal broker/network errors cannot be recovered; surface them to
                        // all ConsumeAsync callers via the channel completion exception.
                        channel.Writer.TryComplete(ex);
                        return;
                    }
                    catch { continue; }

                    if (result?.Message == null) continue;

                    MessageEnvelope envelope;
                    try   { envelope = ParseEnvelope(result.Message); }
                    catch { _consumer!.Commit(result); continue; }

                    _consumer!.Commit(result);
                    channel.Writer.TryWrite(envelope);
                }
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        });

        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
            yield return item;
    }

    private static MessageEnvelope ParseEnvelope(Message<string, byte[]> message)
    {
        return new MessageEnvelope
        {
            EntityType    = GetHeader(message.Headers, "entity_type"),
            EntityId      = GetHeader(message.Headers, "entity_id"),
            ChangeType    = Enum.Parse<ChangeType>(GetHeader(message.Headers, "change_type")),
            CorrelationId = TryParseGuid(GetHeaderBytes(message.Headers, "correlation_id")),
            Version       = int.TryParse(GetHeader(message.Headers, "version"), out var v) ? v : 0,
            Timestamp     = DateTime.TryParse(GetHeader(message.Headers, "timestamp"), out var ts)
                ? ts : DateTime.UtcNow,
            Payload       = message.Value
        };
    }

    private static string GetHeader(Headers headers, string key)
    {
        var bytes = GetHeaderBytes(headers, key);
        return bytes != null ? Encoding.UTF8.GetString(bytes) : string.Empty;
    }

    private static byte[]? GetHeaderBytes(Headers headers, string key)
    {
        foreach (var header in headers)
            if (header.Key == key) return header.GetValueBytes();
        return null;
    }

    private static Guid TryParseGuid(byte[]? bytes)
    {
        if (bytes == null || bytes.Length != 16) return Guid.Empty;
        try { return new Guid(bytes); }
        catch { return Guid.Empty; }
    }

    public void Dispose()
    {
        // Signal the poll loop to stop, then wait for it to exit before freeing
        // the native librdkafka handle — prevents AccessViolationException.
        _disposeCts.Cancel();
        var waitMs = _options.PollTimeoutMs * 2 + 200;
        _pollTask?.Wait(TimeSpan.FromMilliseconds(waitMs));

        _consumer?.Close();
        _consumer?.Dispose();
        _disposeCts.Dispose();
    }
}
