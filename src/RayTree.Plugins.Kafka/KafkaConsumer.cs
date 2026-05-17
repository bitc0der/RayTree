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
    /// <summary>
    /// Discriminator for the post-handler action the poll thread must perform on a
    /// <see cref="ConsumeResult{TKey, TValue}"/> handed back by the subscriber.
    /// </summary>
    private enum PostHandlerAction
    {
        /// <summary>Successful handler dispatch — commit this offset.</summary>
        Commit,
        /// <summary>Handler failed (NACK) — seek back so the broker redelivers in this consumer's lifetime.</summary>
        SeekBack
    }

    private readonly KafkaConsumerOptions _options;
    private readonly ILogger<KafkaConsumer> _logger;
    private readonly CancellationTokenSource _disposeCts = new();
    private IConsumer<string, byte[]>? _consumer;
    private Task? _pollTask;
    private volatile bool _assigned;

    // When AckAfterHandler = true, the subscriber posts the original ConsumeResult here
    // and the poll thread drains the channel each iteration and calls Commit/Seek on its
    // own thread — librdkafka requires Consume/Commit/Seek to share a thread.
    // SingleReader = true because only the poll thread drains; multi-writer (subscriber
    // workers) so we do NOT set SingleWriter.
    private readonly Channel<(ConsumeResult<string, byte[]> Result, PostHandlerAction Action)> _postHandlerChannel =
        Channel.CreateUnbounded<(ConsumeResult<string, byte[]>, PostHandlerAction)>(
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

        // All Confluent.Kafka operations (Consume + Commit + Seek) must run on the same
        // thread. A dedicated background thread polls and buffers envelopes via an
        // unbounded channel. Linking with _disposeCts ensures Dispose() can drain the
        // poll loop before freeing native memory, preventing AccessViolationException.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var linkedToken = linkedCts.Token;

        var channel = Channel.CreateUnbounded<MessageEnvelope>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        // When the post-handler queue is non-empty, drop the poll timeout to zero so we
        // process pending Commits/Seeks immediately instead of waiting up to PollTimeoutMs.
        // This is the latency-cutting trick: handler completion → next iteration → commit.
        var fullTimeout = TimeSpan.FromMilliseconds(_options.PollTimeoutMs);

        _pollTask = Task.Run(() =>
        {
            try
            {
                while (!linkedToken.IsCancellationRequested)
                {
                    // Drain any pending Commits / Seeks — must happen on this thread.
                    DrainPostHandlerQueue();

                    ConsumeResult<string, byte[]>? result;
                    try
                    {
                        // If commits are still queued (e.g. arrived between Drain and now),
                        // use a zero-timeout poll so we cycle back and process them.
                        var effectiveTimeout = _options.AckAfterHandler && _postHandlerChannel.Reader.Count > 0
                            ? TimeSpan.Zero
                            : fullTimeout;

                        result = _consumer!.Consume(effectiveTimeout);
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
                        // Defer commit — AcknowledgeAsync will hand this result back via _postHandlerChannel.
                        envelope.SetConsumeResult(result);
                    }
                    else
                    {
                        // At-most-once (legacy default): commit before handing off.
                        _consumer!.Commit(result);
                    }
                    channel.Writer.TryWrite(envelope);
                }

                // Final drain — flush any commits / seeks pending at shutdown so we don't
                // lose confirmation of work that did complete before cancellation fired.
                DrainPostHandlerQueue();
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
    /// Runs on the poll thread only. Drains every pending post-handler action and
    /// applies it via the corresponding librdkafka call (<c>Commit</c> or <c>Seek</c>).
    /// Exceptions are logged and swallowed per action — one bad commit/seek must not
    /// abort the entire batch.
    /// </summary>
    private void DrainPostHandlerQueue()
    {
        if (!_options.AckAfterHandler) return;

        while (_postHandlerChannel.Reader.TryRead(out var item))
        {
            try
            {
                switch (item.Action)
                {
                    case PostHandlerAction.Commit:
                        _consumer!.Commit(item.Result);
                        break;

                    case PostHandlerAction.SeekBack:
                        // Reset the consumer's local position to this message's offset so
                        // the very next Consume() in this process re-reads it. Without
                        // this, the consumer would have to die and rejoin the group before
                        // Kafka redelivered an un-committed offset.
                        _consumer!.Seek(item.Result.TopicPartitionOffset);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Deferred Kafka {Action} failed at offset {Offset} on topic {Topic}",
                    item.Action, item.Result.Offset, _options.Topic);
            }
        }
    }

    /// <summary>
    /// Schedules the offset commit for the delivery associated with <paramref name="envelope"/>
    /// to run on the poll thread. No-op when <see cref="KafkaConsumerOptions.AckAfterHandler"/>
    /// is <c>false</c> (the offset was already committed inline in the poll loop) or when
    /// the envelope carries no consume-result metadata (e.g. parse-failure path, or a
    /// double-Ack attempt — the metadata is removed on first take).
    /// </summary>
    public Task AcknowledgeAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (!_options.AckAfterHandler) return Task.CompletedTask;
        if (!envelope.TryTakeConsumeResult(out var result) || result is null) return Task.CompletedTask;

        // Post to the poll thread; the actual Commit runs there on the next iteration.
        if (!_postHandlerChannel.Writer.TryWrite((result, PostHandlerAction.Commit)))
        {
            // Only fails if the channel was completed (i.e. KafkaConsumer is disposing).
            // Worth a Debug log so disposal-race silent drops are diagnosable.
            _logger.LogDebug(
                "Skipped deferred commit at offset {Offset} on topic {Topic}: post-handler channel is completed (consumer is disposing)",
                result.Offset, _options.Topic);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Negative-ack: schedule a <c>Seek</c> back to this message's offset on the poll
    /// thread so the broker redelivers it (and everything after) to this very consumer
    /// instance — without requiring a process restart or partition reassignment.
    /// No-op when <see cref="KafkaConsumerOptions.AckAfterHandler"/> is <c>false</c>
    /// (the offset already advanced inline and cannot be rolled back) or when the
    /// envelope carries no consume-result metadata.
    /// </summary>
    public Task NegativeAcknowledgeAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (!_options.AckAfterHandler) return Task.CompletedTask;
        if (!envelope.TryTakeConsumeResult(out var result) || result is null) return Task.CompletedTask;

        if (!_postHandlerChannel.Writer.TryWrite((result, PostHandlerAction.SeekBack)))
        {
            _logger.LogDebug(
                "Skipped deferred seek at offset {Offset} on topic {Topic}: post-handler channel is completed (consumer is disposing)",
                result.Offset, _options.Topic);
        }
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
        _postHandlerChannel.Writer.TryComplete();
    }
}
