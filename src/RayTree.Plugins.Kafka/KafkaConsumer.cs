using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.Kafka;

public class KafkaConsumer : IQueueConsumer, IDisposable
{
    private readonly KafkaConsumerOptions _options;
    private readonly ILogger<KafkaConsumer> _logger;
    private readonly CancellationTokenSource _disposeCts = new();
    private IConsumer<string, byte[]>? _consumer;
    private Task? _pollTask;
    private volatile bool _assigned;

    // When AckAfterHandler = true, the subscriber posts the original ConsumeResult here
    // and the poll thread drains the channel each iteration and calls Commit on its own
    // thread — librdkafka requires Consume and Commit to share a thread.
    private readonly Channel<ConsumeResult<string, byte[]>> _commitChannel =
        Channel.CreateUnbounded<ConsumeResult<string, byte[]>>(
            new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// Returns <see langword="true"/> once the poll loop has made at least one successful
    /// call to <c>Consume()</c>, which indicates that the Kafka broker has acknowledged the
    /// subscription and partition assignment is underway.  Tests can poll this property
    /// instead of using a fixed <see cref="Task.Delay"/> before publishing.
    /// </summary>
    public bool IsAssigned => _assigned;

    public KafkaConsumer(KafkaConsumerOptions options, ILoggerFactory loggerFactory)
    {
        _options = options       ?? throw new ArgumentNullException(nameof(options));
        _logger  = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
                       .CreateLogger<KafkaConsumer>();
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
                    // Drain any pending deferred commits — must happen on this thread.
                    if (_options.AckAfterHandler)
                    {
                        while (_commitChannel.Reader.TryRead(out var pending))
                        {
                            try { _consumer!.Commit(pending); }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex,
                                    "Deferred Kafka commit failed at offset {Offset} on topic {Topic}",
                                    pending.Offset, _options.Topic);
                            }
                        }
                    }

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
                        _logger.LogError(ex, "Fatal Kafka error on topic {Topic}", _options.Topic);
                        channel.Writer.TryComplete(ex);
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error consuming from Kafka topic {Topic}, continuing", _options.Topic);
                        continue;
                    }

                    if (result?.Message == null) continue;

                    MessageEnvelope envelope;
                    try   { envelope = ParseEnvelope(result.Message); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse Kafka message envelope on topic {Topic}, skipping", _options.Topic);
                        // Bad message: commit immediately regardless of AckAfterHandler so it
                        // doesn't poison-pill the partition. Parse errors are not transient.
                        _consumer!.Commit(result);
                        continue;
                    }

                    if (_options.AckAfterHandler)
                    {
                        // Defer commit — AcknowledgeAsync will hand this result back via _commitChannel.
                        envelope.SetConsumeResult(result);
                    }
                    else
                    {
                        // At-most-once (legacy default): commit before handing off.
                        _consumer!.Commit(result);
                    }
                    channel.Writer.TryWrite(envelope);
                }

                // Final drain — flush any commits pending at shutdown so we don't lose
                // confirmation of work that did complete before cancellation fired.
                if (_options.AckAfterHandler)
                {
                    while (_commitChannel.Reader.TryRead(out var pending))
                    {
                        try { _consumer!.Commit(pending); }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "Final-drain Kafka commit failed at offset {Offset} on topic {Topic}",
                                pending.Offset, _options.Topic);
                        }
                    }
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

    /// <summary>
    /// Schedules the offset commit for the delivery associated with <paramref name="envelope"/>
    /// to run on the poll thread. No-op when <see cref="KafkaConsumerOptions.AckAfterHandler"/>
    /// is <c>false</c> (the offset was already committed inline in the poll loop) or when
    /// the envelope carries no consume-result metadata.
    /// </summary>
    public Task AcknowledgeAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (!_options.AckAfterHandler) return Task.CompletedTask;
        if (!envelope.TryGetConsumeResult(out var result) || result is null) return Task.CompletedTask;

        // Post to the poll thread; the actual Commit runs there on the next iteration.
        _commitChannel.Writer.TryWrite(result);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Negative-ack: drop the consume result without committing so the offset stays at
    /// the previous commit and Kafka redelivers the message on the next read from this
    /// consumer group. No-op when <see cref="KafkaConsumerOptions.AckAfterHandler"/> is
    /// <c>false</c> (the offset already advanced inline).
    /// </summary>
    public Task NegativeAcknowledgeAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        // For Kafka, "NACK" means "don't commit". The consume result is intentionally
        // discarded — the partition's committed offset stays put and the broker will
        // re-deliver this message (and everything after) on the next read.
        return Task.CompletedTask;
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
        _commitChannel.Writer.TryComplete();
    }
}
