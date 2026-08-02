using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Telemetry;

namespace RayTree.Core.Distribution;

/// <summary>
/// Drains an outbox of one entity type and publishes its changes to a queue. The public
/// shape is non-generic so callers don't have to thread <c>typeof(TEntity)</c> through their
/// composition root, but all the actual work happens in a <c>TypedImpl&lt;TEntity&gt;</c>
/// instantiated once at construction. Reflection lives at the constructor boundary only —
/// the per-batch publish path is zero-reflection and zero-allocation per call.
/// </summary>
public class OutboxPublisherService : IDisposable
{
    private readonly ITypedImpl _impl;

    public OutboxPublisherService(
        ChangePublisher        publisher,
        Type                   entityType,
        OutboxPublisherOptions options,
        ILoggerFactory         loggerFactory,
        RayTreeMeter           meter)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(meter);

        // One-shot reflection at construction: build the strongly-typed implementation for
        // this entity type. After this point every call into the impl is direct, no
        // MakeGenericMethod / Invoke involved in the per-batch hot path.
        var implType = typeof(TypedImpl<>).MakeGenericType(entityType);
        _impl = (ITypedImpl)Activator.CreateInstance(
            implType, publisher, options, loggerFactory, meter)!;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
        => _impl.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default)
        => _impl.StopAsync(cancellationToken);

    public void Dispose() => _impl.Dispose();

    /// <summary>
    /// Non-generic seam that lets <see cref="OutboxPublisherService"/> hold a single field
    /// of the typed impl regardless of <typeparamref name="TEntity"/>. All members here are
    /// the public surface — nothing leaks the entity type out of the impl.
    /// </summary>
    private interface ITypedImpl : IDisposable
    {
        Task StartAsync(CancellationToken cancellationToken);
        Task StopAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Strongly-typed implementation. All <c>IOutbox.GetUnpublishedAsync&lt;TEntity&gt;</c>
    /// and <c>IChangeSerializer.SerializeAsync&lt;TEntity&gt;</c> calls are direct.
    /// </summary>
    private sealed class TypedImpl<TEntity> : ITypedImpl where TEntity : class
    {
        private readonly ChangePublisher _publisher;
        private readonly OutboxPublisherOptions _options;
        private readonly ILogger<OutboxPublisherService> _logger;
        private readonly RayTreeMeter _meter;
        private readonly KeyValuePair<string, object?> _entityTag;
        private readonly CancellationTokenSource _cts = new();
        private Task? _pollingTask;
        private volatile bool _stopping;
        private DateTime _lastCleanup = DateTime.MinValue;

        // Connection-fault transition state. null = healthy, non-null = the start of the
        // current fault cycle. Single field doubles as "are we unhealthy?" and "when did
        // the cycle start?" — same pattern used by ListenLoopAsync in
        // NotificationBasedPublisher. Accessed only from the single polling task so no
        // synchronization is required.
        private DateTime? _unhealthySince;

        public TypedImpl(
            ChangePublisher publisher,
            OutboxPublisherOptions options,
            ILoggerFactory loggerFactory,
            RayTreeMeter meter)
        {
            _publisher  = publisher;
            _options    = options;
            _meter      = meter;
            _logger     = loggerFactory.CreateLogger<OutboxPublisherService>();
            _entityTag  = RayTreeMeter.EntityTag(typeof(TEntity));
        }

        /// <summary>
        /// When NOTIFY/LISTEN is the fast path, the polling loop runs at the slower fallback
        /// cadence; otherwise it runs at <see cref="OutboxPublisherOptions.PollingInterval"/>.
        /// </summary>
        private TimeSpan EffectivePollingInterval =>
            _options.UseNotificationChannel && _options.FallbackPollingInterval.HasValue
                ? _options.FallbackPollingInterval.Value
                : _options.PollingInterval;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting outbox publisher for {EntityType}", typeof(TEntity).Name);
            _pollingTask = PollAndPublishAsync(_cts.Token);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping outbox publisher for {EntityType}", typeof(TEntity).Name);
            _stopping = true;
            _cts.Cancel();

            if (_pollingTask is not null)
                await Task.WhenAny(_pollingTask, Task.Delay(30000, cancellationToken));
        }

        private async Task PollAndPublishAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var batchWasFull = false;
                try
                {
                    if (!_stopping)
                    {
                        batchWasFull = await ProcessBatchAsync(cancellationToken);

                        if (_unhealthySince is not null)
                            EmitOutboxRecovered();

                        await MaybeRunCleanupAsync(cancellationToken);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    HandleBatchError(ex);
                }

                // Full batch → drain backlog immediately, skip the inter-batch sleep.
                if (batchWasFull) continue;

                try
                {
                    await Task.Delay(EffectivePollingInterval, cancellationToken);
                }
                catch (OperationCanceledException) { break; }
            }
        }

        // Returns true when the batch was full, signalling the caller to loop immediately.
        private async Task<bool> ProcessBatchAsync(CancellationToken cancellationToken)
        {
            var outbox     = _publisher.GetOutbox(typeof(TEntity));
            var queue      = _publisher.GetPublisher(typeof(TEntity));
            var serializer = _publisher.GetSerializer(typeof(TEntity));
            var compressor = _publisher.GetCompressor(typeof(TEntity));

            // Direct generic call — no reflection.
            var changes = await outbox.GetUnpublishedAsync<TEntity>(_options.BatchSize, cancellationToken);
            _meter.OutboxBatchSize.Record(changes.Count, _entityTag);

            // Publishes still happen one message at a time (that's the broker round-trip),
            // but the "mark published" DB write is collected and flushed once per batch
            // instead of once per message — turns N round-trips into 1.
            var publishedIds = new ConcurrentBag<long>();
            try
            {
                await Parallel.ForEachAsync(changes,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = _options.MaxPublishConcurrency,
                        CancellationToken      = cancellationToken
                    },
                    async (change, token) =>
                        await PublishWithRetryAsync(change, queue, serializer, compressor, publishedIds, token));
            }
            finally
            {
                // Best-effort, unconditional: flush whatever succeeded even if some messages in
                // this batch ultimately failed all retries (which makes ProcessBatchAsync throw),
                // or the loop was cancelled — otherwise successfully-published messages would be
                // republished from scratch on the next tick.
                if (!publishedIds.IsEmpty)
                    await outbox.MarkPublishedBatchAsync(publishedIds, CancellationToken.None);
            }

            return changes.Count == _options.BatchSize;
        }

        private async Task PublishWithRetryAsync(
            EntityChange<TEntity> change,
            IQueuePublisher queue,
            IChangeSerializer serializer,
            IChangeCompressor compressor,
            ConcurrentBag<long> publishedIds,
            CancellationToken ct)
        {
            var changeTag = RayTreeMeter.ChangeTag(change.ChangeType);
            var attempts = 0;
            while (attempts < _options.MaxRetryCount)
            {
                attempts++;
                var sw = Stopwatch.StartNew();
                try
                {
                    await PublishChangeAsync(change, queue, serializer, compressor, ct);
                    sw.Stop();
                    _meter.OutboxPublishDuration.Record(sw.Elapsed.TotalSeconds, _entityTag, changeTag);

                    publishedIds.Add(change.Id);

                    _meter.OutboxPublished.Add(1, _entityTag, changeTag);
                    _meter.OutboxPublishAttempts.Record(attempts, _entityTag);
                    _meter.OutboxLagDuration.Record(
                        Math.Max(0, (DateTime.UtcNow - change.Timestamp).TotalSeconds),
                        _entityTag);
                    return;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _meter.OutboxPublishDuration.Record(sw.Elapsed.TotalSeconds, _entityTag, changeTag);

                    if (attempts >= _options.MaxRetryCount)
                    {
                        _meter.OutboxFailed.Add(1, _entityTag, changeTag);
                        // Record the attempts histogram on the failure path too so dashboards
                        // showing "P99 attempts to publish" reflect worst cases, not just successes.
                        _meter.OutboxPublishAttempts.Record(attempts, _entityTag);
                        _logger.LogError(ex,
                            "Failed to publish change {ChangeId} for {EntityType} after {Retries} attempt(s)",
                            change.Id, typeof(TEntity).Name, attempts);
                        throw;
                    }

                    _logger.LogWarning(ex,
                        "Retry {Attempt} of {MaxRetries} failed for {EntityType}, retrying",
                        attempts, _options.MaxRetryCount, typeof(TEntity).Name);
                    await Task.Delay(_options.RetryDelay * attempts, ct);
                }
            }
        }

        private async Task PublishChangeAsync(
            EntityChange<TEntity> change,
            IQueuePublisher queue,
            IChangeSerializer serializer,
            IChangeCompressor compressor,
            CancellationToken ct)
        {
            using var serialized = new MemoryStream();
            // Direct generic call — no reflection.
            await serializer.SerializeAsync(change, serialized, ct);
            serialized.Position = 0;

            using var compressed = new MemoryStream();
            await compressor.CompressAsync(serialized, compressed, ct);

            var envelope = new MessageEnvelope
            {
                EntityType    = change.EntityType,
                EntityId      = change.EntityId,
                ChangeType    = change.ChangeType,
                CorrelationId = change.CorrelationId,
                Version       = change.Version,
                Timestamp     = change.Timestamp,
                Payload       = compressed.ToArray()
            };

            _meter.OutboxPayloadSize.Record(
                envelope.Payload.Length, _entityTag, RayTreeMeter.ChangeTag(change.ChangeType));

            await queue.PublishAsync(envelope, ct);
        }

        private async Task MaybeRunCleanupAsync(CancellationToken cancellationToken)
        {
            if (DateTime.UtcNow - _lastCleanup < _options.CleanupInterval) return;

            var outbox = _publisher.GetOutbox(typeof(TEntity));
            _logger.LogDebug("Outbox rotation starting for {EntityType} (retention: {Retention})",
                typeof(TEntity).Name, _options.CleanupRetentionPeriod);

            var publishedSucceeded = await TryCleanupAsync(
                action:       () => outbox.CleanupPublishedAsync(_options.CleanupRetentionPeriod, cancellationToken),
                counter:      _meter.OutboxRecordsCleaned,
                successLabel: "published",
                level:        LogLevel.Information);

            var staleSucceeded = true;
            if (_options.StaleUnpublishedThreshold is { } threshold)
            {
                staleSucceeded = await TryCleanupAsync(
                    action:       () => outbox.CleanupStaleUnpublishedAsync(threshold, cancellationToken),
                    counter:      _meter.OutboxStaleUnpublishedRemoved,
                    successLabel: $"stale unpublished older than {threshold}",
                    level:        LogLevel.Warning);
            }

            // Only advance the clock when both cleanup operations succeed. A failed cleanup
            // leaves _lastCleanup unchanged so the next polling tick retries — gives operators
            // fast feedback via repeated error logs rather than silently waiting a full interval.
            if (publishedSucceeded && staleSucceeded)
                _lastCleanup = DateTime.UtcNow;
        }

        /// <summary>
        /// Runs one cleanup operation and emits the result. Returns <see langword="false"/> when
        /// the operation throws (logged at <c>Error</c>); the caller uses this to gate
        /// <c>_lastCleanup</c> advancement so a failed cleanup is retried on the next tick.
        /// </summary>
        private async Task<bool> TryCleanupAsync(
            Func<Task<int>> action,
            System.Diagnostics.Metrics.Counter<long> counter,
            string successLabel,
            LogLevel level)
        {
            try
            {
                var deleted = await action();
                if (deleted > 0)
                {
                    counter.Add(deleted, _entityTag);
                    _logger.Log(level,
                        "Outbox rotation removed {Count} {Label} record(s) for {EntityType}",
                        deleted, successLabel, typeof(TEntity).Name);
                }
                else
                {
                    _logger.LogDebug(
                        "Outbox rotation found no {Label} records to remove for {EntityType}",
                        successLabel, typeof(TEntity).Name);
                }
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "Outbox {Label} cleanup failed for {EntityType}",
                    successLabel, typeof(TEntity).Name);
                return false;
            }
        }

        /// <summary>
        /// Inspects an exception thrown from a polling batch. Connection-level faults
        /// (as classified by the outbox's own <see cref="IOutbox.IsConnectionFault"/>)
        /// are logged at <c>Warning</c> and tracked as a recovery cycle. All other
        /// exceptions retain the existing <c>Error</c> log path.
        /// </summary>
        private void HandleBatchError(Exception ex)
        {
            // Parallel.ForEachAsync may surface a wrapped AggregateException when one or
            // more inner publish calls threw; unwrap the first inner so the classifier sees
            // the real cause (e.g. NpgsqlException with a connection-level SqlState).
            var rootCause = ex switch
            {
                AggregateException agg when agg.InnerException is not null => agg.InnerException,
                _                                                          => ex
            };

            // Resolve the outbox defensively — during shutdown / disposal `GetOutbox` could
            // itself throw. Don't mask the real batch error with a lookup failure.
            IOutbox? outbox = null;
            try { outbox = _publisher.GetOutbox(typeof(TEntity)); }
            catch { /* fall through to non-classified path below */ }

            if (outbox is not null
                && outbox.IsConnectionFault(rootCause)
                && outbox.ConnectionComponent is { } component)
            {
                var endpoint = outbox.ConnectionEndpoint ?? "<unknown>";
                if (_unhealthySince is null)
                    _unhealthySince = DateTime.UtcNow;
                _logger.LogWarning(rootCause,
                    "Outbox connection fault for {EntityType} ({Component} at {Endpoint}); polling will retry on next tick",
                    typeof(TEntity).Name, component, endpoint);
            }
            else
            {
                _logger.LogError(ex, "Error processing outbox batch for {EntityType}", typeof(TEntity).Name);
            }
        }

        /// <summary>
        /// Emits the connection-recovery log entry, then clears the unhealthy flag.
        /// Called from the polling loop on the first successful batch following a fault.
        /// </summary>
        private void EmitOutboxRecovered()
        {
            // Always clear the flag — even if the outbox lookup fails (shutdown). Otherwise
            // we'd loop emitting the recovery log on every subsequent successful batch.
            try
            {
                var outbox    = _publisher.GetOutbox(typeof(TEntity));
                var component = outbox.ConnectionComponent ?? "outbox";
                var endpoint  = outbox.ConnectionEndpoint  ?? "<unknown>";
                // Clamp at zero to defend against backward clock jumps.
                var duration  = Math.Max(0, (DateTime.UtcNow - _unhealthySince!.Value).TotalSeconds);
                _logger.LogInformation(
                    "Outbox connection recovered for {EntityType} ({Component} at {Endpoint}) after {Duration:F2}s",
                    typeof(TEntity).Name, component, endpoint, duration);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to emit outbox recovery log for {EntityType}; clearing unhealthy flag anyway",
                    typeof(TEntity).Name);
            }
            finally
            {
                _unhealthySince = null;
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
