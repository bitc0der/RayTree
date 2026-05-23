using System.Diagnostics;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;

namespace RayTree.Plugins.Kafka;

/// <summary>
/// Probes a Kafka broker for the existence of a topic with passive metadata calls and waits
/// for it to appear. Used by <see cref="KafkaPublisher"/> and <see cref="KafkaConsumer"/> when
/// configured with <c>WaitForTopic = true</c> so a service consuming an externally-owned topic
/// does not crash on startup if the owning service has not yet created it.
/// </summary>
internal static class KafkaTopicProbe
{
    /// <summary>
    /// Upper bound on a single <c>GetMetadata</c> call's blocking duration, decoupled from
    /// <c>TopicWaitInterval</c>. Keeping this short ensures: (a) cancellation between attempts is
    /// observed within ~1 s of the token firing (worst-case still bounded by the inter-attempt
    /// <c>Task.Delay</c>); (b) the option's "wait may exceed by up to one TopicWaitInterval"
    /// contract holds even on slow / unreachable brokers; (c) threadpool threads pinned in
    /// blocking librdkafka calls during shutdown release in roughly one second.
    /// </summary>
    private static readonly TimeSpan MetadataCallTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Wait until the named topic is reported as available by the broker, retrying on:
    /// <list type="bullet">
    ///   <item>Empty <c>Topics</c> collection in the metadata response.</item>
    ///   <item>Per-topic <c>ErrorCode.UnknownTopicOrPart</c>.</item>
    ///   <item>Per-topic <c>ErrorCode.LeaderNotAvailable</c> (cluster bootstrap / leader election).</item>
    ///   <item>Transient transport-level <c>KafkaException</c>s thrown by <c>GetMetadata</c>:
    ///         <c>Local_Transport</c> (broker socket refused / closed), <c>Local_AllBrokersDown</c>,
    ///         <c>Local_Resolve</c> (DNS not yet resolved), <c>Local_TimedOut</c>. These are the
    ///         dominant microservice startup-ordering case where the broker pod has not yet
    ///         finished starting.</item>
    /// </list>
    /// All other broker error codes, fatal <c>KafkaException</c>s (<c>Error.IsFatal == true</c>),
    /// and <see cref="OperationCanceledException"/> propagate immediately without retry.
    /// <para>
    /// <b>Cancellation latency:</b> token cancellation between attempts is observed promptly via
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>. Cancellation during an in-flight
    /// metadata call is bounded by <see cref="MetadataCallTimeout"/> (~1 s) — librdkafka does
    /// not honour managed cancellation tokens mid-call.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="interval"/> is not positive, or <paramref name="timeout"/> is set and not positive.</exception>
    /// <exception cref="OperationCanceledException">When <paramref name="cancellationToken"/> is cancelled (including before the first attempt).</exception>
    /// <exception cref="KafkaException">For non-retryable broker errors, fatal librdkafka errors, or when <paramref name="timeout"/> elapses without success.</exception>
    public static async Task WaitForTopicAsync(
        string bootstrapServers,
        string topic,
        TimeSpan interval,
        TimeSpan? timeout,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Topic wait interval must be positive.");
        if (timeout is { } t && t <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Topic wait timeout must be positive when set.");

        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        var missCount = 0;
        KafkaException? lastException = null;

        var adminConfig = new AdminClientConfig { BootstrapServers = bootstrapServers };
        IAdminClient admin = new AdminClientBuilder(adminConfig).Build();

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Metadata call uses a small fixed timeout (decoupled from TopicWaitInterval) so
                // (a) cancellation is observed within ~1s even mid-call and (b) the inter-attempt
                // sleep is the dominant pacing knob — overshoot is bounded by ~1 interval, not 2.
                Metadata? metadata = null;
                KafkaException? transportException = null;
                try
                {
                    metadata = await Task.Run(() => admin.GetMetadata(topic, MetadataCallTimeout), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (KafkaException ex) when (ex.Error.IsFatal)
                {
                    // Fatal librdkafka error (invalid configuration, unrecoverable client state):
                    // cannot make progress. Propagate.
                    throw;
                }
                catch (KafkaException ex) when (IsRetryableTransportError(ex.Error))
                {
                    // Broker not yet reachable / DNS failure / all-brokers-down / call timed out.
                    // Treat exactly like a topic-missing miss: log, sleep, retry.
                    transportException = ex;
                }

                bool isMiss;
                KafkaException? missException;
                if (transportException is not null)
                {
                    isMiss = true;
                    missException = transportException;
                }
                else
                {
                    var entry = metadata!.Topics.FirstOrDefault(x => x.Topic == topic);
                    (isMiss, missException) = ClassifyResponse(topic, entry);
                }

                if (!isMiss)
                {
                    if (missCount > 0)
                    {
                        logger?.LogInformation(
                            "Kafka topic '{Topic}' became available after {Misses} miss(es) ({Elapsed}).",
                            topic, missCount, stopwatch.Elapsed);
                    }
                    return;
                }

                lastException = missException ?? lastException;
                missCount++;

                if (missCount == 1)
                {
                    logger?.LogInformation(
                        "Kafka topic '{Topic}' not found yet; waiting (interval {Interval}, timeout {Timeout}).",
                        topic, interval, timeout?.ToString() ?? "<none>");
                }
                else
                {
                    logger?.LogDebug(
                        "Kafka topic '{Topic}' still missing after {Misses} attempts ({Elapsed}).",
                        topic, missCount, stopwatch.Elapsed);
                }

                if (timeout is { } limit && stopwatch.Elapsed >= limit)
                {
                    logger?.LogError(
                        "Kafka topic wait for '{Topic}' timed out after {Elapsed} (limit {Limit}).",
                        topic, stopwatch.Elapsed, limit);
                    throw lastException ?? SynthesiseUnknownTopicException(topic);
                }

                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            admin.Dispose();
        }
    }

    /// <summary>
    /// Classifies a single metadata response. Retryable misses: missing entry, UnknownTopicOrPart,
    /// or LeaderNotAvailable. Non-retryable per-topic errors are thrown as KafkaException.
    /// </summary>
    private static (bool isMiss, KafkaException? missException) ClassifyResponse(string topic, TopicMetadata? entry)
    {
        // Missing entry (some broker versions return empty Topics on unknown topic).
        if (entry is null)
            return (true, null);

        var code = entry.Error.Code;

        if (code == ErrorCode.NoError)
            return (false, null);

        if (code == ErrorCode.UnknownTopicOrPart || code == ErrorCode.LeaderNotAvailable)
            return (true, new KafkaException(entry.Error));

        // Any other per-topic error code is non-retryable (TopicAuthorizationFailed, etc.).
        throw new KafkaException(entry.Error);
    }

    /// <summary>
    /// Classify a thrown <see cref="KafkaException"/> as a retryable transport-level error.
    /// Covers the startup-ordering window where the broker is briefly unreachable: connection
    /// refusal, DNS resolve failure, all-brokers-down, and single-call timeouts. Excludes fatal
    /// errors (handled in a separate catch) and per-topic broker errors (which surface inside
    /// the metadata response, not as a thrown exception).
    /// </summary>
    private static bool IsRetryableTransportError(Error error)
    {
        if (error.IsFatal) return false;
        return error.Code is
            ErrorCode.Local_Transport
            or ErrorCode.Local_AllBrokersDown
            or ErrorCode.Local_Resolve
            or ErrorCode.Local_TimedOut;
    }

    private static KafkaException SynthesiseUnknownTopicException(string topic) =>
        new(new Error(ErrorCode.UnknownTopicOrPart, $"Topic '{topic}' was not found within the configured topic-wait timeout."));
}
