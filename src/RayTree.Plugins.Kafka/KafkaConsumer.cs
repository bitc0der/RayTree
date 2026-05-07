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

    public async IAsyncEnumerable<(EntityChange Change, byte[] Payload)> ConsumeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // All Confluent.Kafka operations (Consume + Commit) must run on the same thread.
        // A dedicated background thread polls and buffers results via an unbounded channel.
        // We link the caller's token with _disposeCts so Dispose() can stop the poll loop
        // before freeing native memory, preventing AccessViolationException.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var linkedToken = linkedCts.Token;

        var channel = Channel.CreateUnbounded<(EntityChange, byte[])>(
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
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException)    { break; }
                    catch                              { continue; }

                    if (result?.Message == null) continue;

                    EntityChange change;
                    try   { change = ParseEntityChange(result.Message); }
                    catch { _consumer!.Commit(result); continue; }

                    _consumer!.Commit(result);
                    channel.Writer.TryWrite((change, result.Message.Value));
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

    private static EntityChange ParseEntityChange(Message<string, byte[]> message)
    {
        return new EntityChange
        {
            EntityType    = GetHeader(message.Headers, "entity_type"),
            EntityId      = GetHeader(message.Headers, "entity_id"),
            ChangeType    = Enum.Parse<ChangeType>(GetHeader(message.Headers, "change_type")),
            CorrelationId = TryParseGuid(GetHeaderBytes(message.Headers, "correlation_id")),
            Version       = int.TryParse(GetHeader(message.Headers, "version"), out var v) ? v : 0,
            Timestamp     = DateTime.TryParse(GetHeader(message.Headers, "timestamp"), out var ts)
                ? ts
                : DateTime.UtcNow
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
