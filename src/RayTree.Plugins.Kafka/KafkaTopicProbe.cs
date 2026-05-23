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
    /// Wait until the named topic is reported as available by the broker, retrying on
    /// transient "not-yet-available" responses (empty Topics, UnknownTopicOrPart,
    /// LeaderNotAvailable). All other broker errors propagate immediately.
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
        // Validate inputs before any side effects (task 2.2).
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

                // Each metadata call blocks for up to `interval`. Run on a worker thread so the
                // caller's sync context isn't pinned (librdkafka does not honour managed tokens
                // mid-call — cancellation is observed at the next decision point).
                Metadata metadata;
                try
                {
                    metadata = await Task.Run(() => admin.GetMetadata(topic, interval), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (KafkaException ex) when (ex.Error.IsFatal)
                {
                    // Fatal: cannot recover.
                    throw;
                }

                var entry = metadata.Topics.FirstOrDefault(x => x.Topic == topic);
                var (isMiss, missException) = ClassifyResponse(topic, entry);

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

    private static KafkaException SynthesiseUnknownTopicException(string topic) =>
        new(new Error(ErrorCode.UnknownTopicOrPart, $"Topic '{topic}' was not found within the configured topic-wait timeout."));
}
