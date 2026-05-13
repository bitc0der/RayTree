using System.Reflection;
using Microsoft.Extensions.Logging;
using RayTree.Core.Models;
using RayTree.Core.Plugins;

using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Serialization;

namespace RayTree.Core.Distribution;

public class OutboxPublisherService : IDisposable
{
    private readonly ChangePublisher _publisher;
    private readonly Type _entityType;
    private readonly OutboxPublisherOptions _options;
    private readonly ILogger<OutboxPublisherService> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollingTask;
    private volatile bool _stopping;
    private DateTime _lastCleanup = DateTime.MinValue;

    private static readonly MethodInfo GetUnpublishedMethod = typeof(OutboxPublisherService)
        .GetMethod(nameof(GetUnpublishedCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo SerializeMethod = typeof(OutboxPublisherService)
        .GetMethod(nameof(SerializeCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    public OutboxPublisherService(
        ChangePublisher publisher,
        Type entityType,
        OutboxPublisherOptions options,
        ILoggerFactory loggerFactory)
    {
        _publisher  = publisher    ?? throw new ArgumentNullException(nameof(publisher));
        _entityType = entityType   ?? throw new ArgumentNullException(nameof(entityType));
        _options    = options      ?? throw new ArgumentNullException(nameof(options));
        _logger     = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
                          .CreateLogger<OutboxPublisherService>();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting outbox publisher for {EntityType}", _entityType.Name);
        _pollingTask = PollAndPublishAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Stopping outbox publisher for {EntityType}", _entityType.Name);
        _stopping = true;
        _cts.Cancel();

        if (_pollingTask != null)
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
                    await MaybeRunCleanupAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing outbox batch for {EntityType}", _entityType.Name);
            }

            // When the batch was full more records are likely waiting — loop immediately
            // rather than sleeping so we drain the backlog without artificial delay.
            if (batchWasFull) continue;

            var delay = _options.UseNotificationChannel && _options.FallbackPollingInterval.HasValue
                ? _options.FallbackPollingInterval.Value
                : _options.PollingInterval;

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    // Returns true when the batch was full, signalling the caller to loop immediately.
    private async Task<bool> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var outbox     = _publisher.GetOutbox(_entityType);
        var publisher  = _publisher.GetPublisher(_entityType);
        var serializer = _publisher.GetSerializer(_entityType);
        var compressor = _publisher.GetCompressor(_entityType);

        var changes = await GetUnpublishedAsync(outbox, _options.BatchSize, cancellationToken);

        await Parallel.ForEachAsync(changes,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.MaxPublishConcurrency,
                CancellationToken      = cancellationToken
            },
            async (change, token) =>
                await PublishWithRetryAsync(change, outbox, publisher, serializer, compressor, token));

        return changes.Count == _options.BatchSize;
    }

    private async Task PublishWithRetryAsync(
        EntityChange change,
        IOutbox outbox,
        IQueuePublisher publisher,
        IChangeSerializer serializer,
        IChangeCompressor compressor,
        CancellationToken ct)
    {
        var retries = 0;
        while (retries < _options.MaxRetryCount)
        {
            try
            {
                await PublishChangeAsync(change, publisher, serializer, compressor, ct);
                await outbox.MarkPublishedAsync(change.Id, ct);
                return;
            }
            catch (Exception ex)
            {
                retries++;
                if (retries >= _options.MaxRetryCount)
                {
                    _logger.LogError(ex,
                        "Failed to publish change {ChangeId} for {EntityType} after {Retries} attempt(s)",
                        change.Id, _entityType.Name, retries);
                    throw;
                }

                _logger.LogWarning(ex,
                    "Retry {Attempt} of {MaxRetries} failed for {EntityType}, retrying",
                    retries, _options.MaxRetryCount, _entityType.Name);
                await Task.Delay(_options.RetryDelay * retries, ct);
            }
        }
    }

    private Task<IReadOnlyList<EntityChange>> GetUnpublishedAsync(IOutbox outbox, int batchSize, CancellationToken ct)
        => (Task<IReadOnlyList<EntityChange>>)GetUnpublishedMethod
            .MakeGenericMethod(_entityType)
            .Invoke(null, [outbox, batchSize, ct])!;

    private static async Task<IReadOnlyList<EntityChange>> GetUnpublishedCoreAsync<TEntity>(
        IOutbox outbox,
        int batchSize,
        CancellationToken cancellationToken)
        where TEntity : class
        => await outbox.GetUnpublishedAsync<TEntity>(batchSize, cancellationToken);

    private async Task PublishChangeAsync(
        EntityChange change,
        IQueuePublisher publisher,
        IChangeSerializer serializer,
        IChangeCompressor compressor,
        CancellationToken ct)
    {
        using var serialized = new MemoryStream();
        await SerializeAsync(serializer, change, serialized, ct);
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

        await publisher.PublishAsync(envelope, ct);
    }

    private Task SerializeAsync(IChangeSerializer serializer, EntityChange change, Stream destination, CancellationToken ct)
        => (Task)SerializeMethod.MakeGenericMethod(_entityType).Invoke(null, [serializer, change, destination, ct])!;

    private static Task SerializeCoreAsync<TEntity>(
        IChangeSerializer serializer,
        EntityChange<TEntity> change,
        Stream destination,
        CancellationToken ct)
        where TEntity : class
        => serializer.SerializeAsync(change, destination, ct);

    private async Task MaybeRunCleanupAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _lastCleanup < _options.CleanupInterval) return;

        var outbox = _publisher.GetOutbox(_entityType);
        var succeeded = true;

        _logger.LogDebug("Outbox rotation starting for {EntityType} (retention: {Retention})",
            _entityType.Name, _options.CleanupRetentionPeriod);

        try
        {
            var deleted = await outbox.CleanupPublishedAsync(_options.CleanupRetentionPeriod, cancellationToken);
            if (deleted > 0)
                _logger.LogInformation("Outbox rotation removed {Deleted} published record(s) for {EntityType}",
                    deleted, _entityType.Name);
            else
                _logger.LogDebug("Outbox rotation found no published records to remove for {EntityType}",
                    _entityType.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            succeeded = false;
            _logger.LogError(ex, "Outbox published cleanup failed for {EntityType}", _entityType.Name);
        }

        if (_options.StaleUnpublishedThreshold is { } threshold)
        {
            try
            {
                var stale = await outbox.CleanupStaleUnpublishedAsync(threshold, cancellationToken);
                if (stale > 0)
                    _logger.LogWarning(
                        "Outbox rotation removed {Count} stale unpublished record(s) for {EntityType} older than {Threshold} — check queue health",
                        stale, _entityType.Name, threshold);
                else
                    _logger.LogDebug("Outbox rotation found no stale unpublished records for {EntityType}",
                        _entityType.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                succeeded = false;
                _logger.LogError(ex, "Outbox stale unpublished cleanup failed for {EntityType}", _entityType.Name);
            }
        }

        if (succeeded)
            _lastCleanup = DateTime.UtcNow;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
