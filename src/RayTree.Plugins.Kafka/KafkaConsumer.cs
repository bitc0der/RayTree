using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Consumer;
using RayTree.Core.Resilience;
using RayTree.Core.Telemetry;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.Kafka;

public class KafkaConsumer : IQueueConsumer, IDisposable
{
    private const string ComponentName = "kafka.consumer";

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
    private readonly RayTreeMeter? _meter;
    private readonly CancellationTokenSource _disposeCts = new();
    private IConsumer<string, byte[]>? _consumer;
    private Task? _pollTask;
    private volatile bool _assigned;
    private volatile bool _connected;
    private readonly IDisposable? _stateGaugeSubscription;

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

    public KafkaConsumer(
        KafkaConsumerOptions options,
        ILoggerFactory       loggerFactory,
        RayTreeMeter?        meter = null)
    {
        _options = options       ?? throw new ArgumentNullException(nameof(options));
        _logger  = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
                       .CreateLogger<KafkaConsumer>();
        _meter   = meter;

        _options.ConnectionRecovery.Validate();

        _stateGaugeSubscription = _meter?.RegisterConnectionStateGauge(
            component: ComponentName,
            endpoint:  _options.BootstrapServers,
            getState:  () => _connected ? 1 : 0);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Probe the topic BEFORE allocating native librdkafka handles so a failed probe
        // (timeout, cancellation, non-retryable error) leaves no state to clean up.
        if (_options.WaitForTopic)
        {
            await KafkaTopicProbe.WaitForTopicAsync(
                _options.BootstrapServers,
                _options.Topic,
                _options.TopicWaitInterval,
                _options.TopicWaitTimeout,
                _logger,
                cancellationToken).ConfigureAwait(false);
        }

        // Honour cancellation in the gap between a slow probe completing and the native
        // consumer handle being allocated — without this, a Ctrl+C just after probe success
        // would leak the librdkafka handle (the pre-probe comment justifies probe-first on
        // the basis that a failed probe leaves no state to clean up).
        cancellationToken.ThrowIfCancellationRequested();

        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId          = _options.GroupId,
            AutoOffsetReset  = _options.FromEarliest ? AutoOffsetReset.Earliest : AutoOffsetReset.Latest,
            EnableAutoCommit = false
        };

        _consumer = new ConsumerBuilder<string, byte[]>(config).Build();
        _consumer.Subscribe(_options.Topic);
        _connected = true;
    }

    /// <summary>
    /// Builds a fresh consumer with the existing configuration and subscribes to the topic.
    /// Used by both <see cref="InitializeAsync"/> and <see cref="RebuildConsumer"/> so the
    /// configuration shape stays identical between initial setup and post-fatal rebuild.
    /// </summary>
    private IConsumer<string, byte[]> BuildConsumer()
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId          = _options.GroupId,
            AutoOffsetReset  = _options.FromEarliest ? AutoOffsetReset.Earliest : AutoOffsetReset.Latest,
            EnableAutoCommit = false
        };
        var c = new ConsumerBuilder<string, byte[]>(config).Build();
        c.Subscribe(_options.Topic);
        return c;
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
                        // Fatal broker error: dispose the dead consumer and rebuild on this
                        // same poll thread. Pending deferred-ack actions reference the dying
                        // consumer and must be dropped — the broker will redeliver via the
                        // standard at-least-once contract once the new consumer joins.
                        _connected = false;
                        _meter?.RecordConnectionDisconnect(ComponentName, _options.BootstrapServers);
                        _logger.LogWarning(ex,
                            "Kafka consumer fatal error on topic {Topic}, rebuilding", _options.Topic);

                        if (!_options.ConnectionRecovery.Enabled)
                        {
                            _logger.LogError(ex,
                                "Kafka consumer recovery disabled; surfacing fatal error to consumers");
                            channel.Writer.TryComplete(ex);
                            return;
                        }

                        // Drop stale post-handler actions before rebuild.
                        while (_postHandlerChannel.Reader.TryRead(out _)) { }

                        if (!RebuildConsumer(linkedToken))
                        {
                            channel.Writer.TryComplete(ex);
                            return;
                        }
                        continue;
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
    /// Runs synchronously on the poll thread after a fatal-error dispose. Disposes the
    /// dying consumer, then runs an exponential-backoff loop bounded by
    /// <c>_options.ConnectionRecovery</c> that re-runs the topic-wait probe (when enabled)
    /// and builds a fresh <c>IConsumer</c>. Returns <see langword="true"/> on success and
    /// <see langword="false"/> when retries are exhausted or cancellation fires — the caller
    /// SHALL surface the failure to <c>ConsumeAsync</c> consumers via channel completion.
    /// </summary>
    private bool RebuildConsumer(CancellationToken ct)
    {
        try { _consumer?.Close(); _consumer?.Dispose(); } catch { /* may already be torn down */ }
        _consumer = null;
        _assigned = false;

        var recovery = _options.ConnectionRecovery;
        var startedAt = DateTime.UtcNow;
        var attempt = 0;

        while (true)
        {
            if (ct.IsCancellationRequested) return false;
            attempt++;

            try
            {
                // Re-run topic-wait probe on rebuild so a broker restart that races with
                // topic recreation is handled — matches the kafka-topic-wait reprobe contract.
                if (_options.WaitForTopic)
                {
                    KafkaTopicProbe.WaitForTopicAsync(
                        _options.BootstrapServers,
                        _options.Topic,
                        _options.TopicWaitInterval,
                        _options.TopicWaitTimeout,
                        _logger,
                        ct).GetAwaiter().GetResult();
                }

                _consumer = BuildConsumer();
                _connected = true;

                var duration = (DateTime.UtcNow - startedAt).TotalSeconds;
                _meter?.RecordConnectionRecovery(ComponentName, _options.BootstrapServers,
                    outcome: "succeeded", duration);
                _logger.LogInformation(
                    "Kafka consumer rebuilt for topic {Topic} after {AttemptCount} attempt(s) in {Duration:F2}s",
                    _options.Topic, attempt, duration);
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                if (recovery.MaxAttempts is int max && attempt >= max)
                {
                    var duration = (DateTime.UtcNow - startedAt).TotalSeconds;
                    _meter?.RecordConnectionRecovery(ComponentName, _options.BootstrapServers,
                        outcome: "exhausted", duration);
                    _logger.LogError(ex,
                        "Kafka consumer rebuild exhausted on topic {Topic} after {AttemptCount} attempts",
                        _options.Topic, attempt);
                    return false;
                }

                var delay = ComputeBackoffDelay(recovery, attempt);
                _logger.LogInformation(ex,
                    "Consumer rebuild attempt {AttemptNumber} failed for {Topic}; retrying in {Delay:F2}s",
                    attempt, _options.Topic, delay.TotalSeconds);
                try { Task.Delay(delay, ct).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { return false; }
            }
        }
    }

    private static TimeSpan ComputeBackoffDelay(ConnectionRecoveryOptions opts, int attemptNum)
    {
        var baseTicks = opts.InitialDelay.Ticks * Math.Pow(opts.Factor, attemptNum - 1);
        var cappedTicks = Math.Min(baseTicks, opts.MaxDelay.Ticks);
        if (opts.JitterFraction <= 0) return TimeSpan.FromTicks((long)cappedTicks);

        var jitterMultiplier = 1.0 + (Random.Shared.NextDouble() * 2 - 1) * opts.JitterFraction;
        return TimeSpan.FromTicks((long)(cappedTicks * jitterMultiplier));
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

        _stateGaugeSubscription?.Dispose();
        _consumer?.Close();
        _consumer?.Dispose();
        _disposeCts.Dispose();
        _postHandlerChannel.Writer.TryComplete();
    }
}
