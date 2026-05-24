using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using RayTree.Core.Distribution;
using RayTree.Core.Models;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Resilience;
using RayTree.Core.Telemetry;
using RayTree.Core.Tracking;
using RayTree.Plugins.PostgreSQL.Internal;

namespace RayTree.Plugins.PostgreSQL.Outbox.Notification;

public class NotificationBasedPublisher : IDisposable
{
    private readonly ChangePublisher _publisher;
    private readonly NotificationBasedPublisherOptions _options;
    private readonly ILogger<NotificationBasedPublisher> _logger;
    private readonly RayTreeMeter _meter;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _notificationSemaphore;
    private NpgsqlConnection? _connection;
    private Task? _listenTask;
    private Task? _fallbackTask;
    private volatile bool _listenerHealthy = true;
    private bool _firstFallbackPoll = true;
    private IDisposable? _stateGaugeSubscription;

    // Per-outbox transition state for the fallback polling loop. Keyed by entity type;
    // the value's unhealthy flag flips on the first connection-fault for that outbox and
    // back to false on the first subsequent successful batch.
    private readonly ConcurrentDictionary<Type, FallbackOutboxState> _fallbackOutboxState = new();

    private const string ComponentName = "postgres.notification";

    private static readonly MethodInfo GetByIdMethod = typeof(NotificationBasedPublisher)
        .GetMethod(nameof(GetByIdCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo GetUnpublishedMethod = typeof(NotificationBasedPublisher)
        .GetMethod(nameof(GetUnpublishedCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo SerializeMethod = typeof(NotificationBasedPublisher)
        .GetMethod(nameof(SerializeCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    public NotificationBasedPublisher(
        EntityChangeTracker tracker,
        NotificationBasedPublisherOptions options,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _publisher             = tracker.Publisher;
        _options               = options      ?? throw new ArgumentNullException(nameof(options));
        _logger                = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
                                     .CreateLogger<NotificationBasedPublisher>();
        _meter                 = _publisher.Meter;
        _notificationSemaphore = new SemaphoreSlim(options.MaxConcurrentNotifications,
                                                   options.MaxConcurrentNotifications);

        // Validate the recovery options eagerly so misconfiguration fails at construction,
        // not on the first disconnect.
        _options.ConnectionRecovery.Validate();

        // Register the connection-state gauge keyed on the LISTEN channel. The closure
        // captures _listenerHealthy so OTel collection sees the live value.
        _stateGaugeSubscription = _meter.RegisterConnectionStateGauge(
            component: ComponentName,
            endpoint:  _options.ChannelName,
            getState:  () => _listenerHealthy ? 1 : 0);
    }

    public bool IsRunning => _listenTask != null && !_listenTask.IsCompleted;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _connection = new NpgsqlConnection(_options.ConnectionString);
        await _connection.OpenAsync(cancellationToken);

        _connection.Notification += OnNotification;

        await using var cmd = new NpgsqlCommand($"LISTEN {_options.ChannelName}", _connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        _listenTask   = ListenLoopAsync(_cts.Token);
        _fallbackTask = FallbackPollingLoopAsync(_cts.Token);

        _logger.LogInformation("NotificationBasedPublisher started, listening on channel {ChannelName}",
            _options.ChannelName);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cts.Cancel();

        if (_listenTask != null)
            await Task.WhenAny(_listenTask, Task.Delay(5000, cancellationToken));

        if (_fallbackTask != null)
            await Task.WhenAny(_fallbackTask, Task.Delay(5000, cancellationToken));

        if (_connection != null)
        {
            try
            {
                await using var cmd = new NpgsqlCommand($"UNLISTEN {_options.ChannelName}", _connection);
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch { }

            await _connection.CloseAsync();
        }

        _logger.LogInformation("NotificationBasedPublisher stopped");
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _connection!.WaitAsync(cancellationToken);
                if (!_listenerHealthy)
                {
                    _listenerHealthy = true;
                    _logger.LogInformation("PostgreSQL LISTEN connection on {ChannelName} recovered",
                        _options.ChannelName);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (IsConnectionFault(ex))
            {
                // Connection fault: emit disconnect on first detection, then reconnect.
                if (_listenerHealthy)
                {
                    _listenerHealthy = false;
                    _meter.RecordConnectionDisconnect(ComponentName, _options.ChannelName);
                    _logger.LogWarning(ex,
                        "PostgreSQL LISTEN connection on {ChannelName} lost, reconnecting",
                        _options.ChannelName);
                }

                if (!_options.ConnectionRecovery.Enabled)
                {
                    // Recovery disabled: surface the disconnect by exiting the loop. The
                    // fallback polling loop continues to drain records; the LISTEN fast
                    // path stays cold until process restart.
                    break;
                }

                try
                {
                    await ReconnectAsync(cancellationToken);
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    // ReconnectAsync exhausted its retry budget. The disconnect counter
                    // and exhausted recovery counter are already emitted; exit the loop
                    // so the surrounding service stops attempting LISTEN. Fallback polling
                    // continues to provide best-effort delivery.
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Runs an inline exponential-backoff loop bounded by <c>_options.ConnectionRecovery</c>
    /// to re-establish the LISTEN connection. Disposes the old connection, opens a fresh one,
    /// re-attaches the <c>Notification</c> handler, and issues <c>LISTEN</c>. On success the
    /// loop's surrounding code resumes <c>WaitAsync</c> against the new connection (and flips
    /// <c>_listenerHealthy</c> back to <c>true</c> on the next successful wake).
    /// </summary>
    private async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        var recovery = _options.ConnectionRecovery;
        var startedAt = DateTime.UtcNow;
        var attemptNum = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attemptNum++;

            try
            {
                // Dispose the broken connection (detach event handler first to avoid
                // a stray notification firing during the swap).
                if (_connection is not null)
                {
                    _connection.Notification -= OnNotification;
                    try { await _connection.DisposeAsync(); } catch { /* may already be broken */ }
                }

                _connection = new NpgsqlConnection(_options.ConnectionString);
                await _connection.OpenAsync(cancellationToken);
                _connection.Notification += OnNotification;

                await using (var cmd = new NpgsqlCommand($"LISTEN {_options.ChannelName}", _connection))
                    await cmd.ExecuteNonQueryAsync(cancellationToken);

                var duration = (DateTime.UtcNow - startedAt).TotalSeconds;
                _meter.RecordConnectionRecovery(ComponentName, _options.ChannelName, outcome: "succeeded", duration);
                _logger.LogInformation(
                    "PostgreSQL LISTEN connection on {ChannelName} reconnected after {AttemptCount} attempt(s) in {Duration:F2}s",
                    _options.ChannelName, attemptNum, duration);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (recovery.MaxAttempts is int max && attemptNum >= max)
                {
                    var duration = (DateTime.UtcNow - startedAt).TotalSeconds;
                    _meter.RecordConnectionRecovery(ComponentName, _options.ChannelName, outcome: "exhausted", duration);
                    _logger.LogError(ex,
                        "PostgreSQL LISTEN reconnect exhausted on {ChannelName} after {AttemptCount} attempts",
                        _options.ChannelName, attemptNum);
                    throw;
                }

                var delay = ComputeBackoffDelay(recovery, attemptNum);
                _logger.LogInformation(ex,
                    "LISTEN reconnect attempt {AttemptNumber} failed for {ChannelName}; retrying in {Delay:F2}s",
                    attemptNum, _options.ChannelName, delay.TotalSeconds);

                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static TimeSpan ComputeBackoffDelay(ConnectionRecoveryOptions opts, int attemptNum)
    {
        var baseTicks = opts.InitialDelay.Ticks * Math.Pow(opts.Factor, attemptNum - 1);
        var cappedTicks = Math.Min(baseTicks, opts.MaxDelay.Ticks);
        if (opts.JitterFraction <= 0) return TimeSpan.FromTicks((long)cappedTicks);

        var rand = Random.Shared.NextDouble();                          // [0, 1)
        var jitterMultiplier = 1.0 + (rand * 2 - 1) * opts.JitterFraction;
        return TimeSpan.FromTicks((long)(cappedTicks * jitterMultiplier));
    }

    /// <summary>
    /// Classifier for LISTEN-side connection faults. Delegates to <see cref="PostgresFault"/>
    /// so the LISTEN path and the outbox path stay consistent.
    /// </summary>
    private static bool IsConnectionFault(Exception ex) => PostgresFault.IsConnectionFault(ex);

    private async Task FallbackPollingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Always run on the first tick to drain records written before the listener
                // was established (e.g., from a previous run). After that, only poll when
                // the LISTEN connection is unhealthy — the notification path handles the rest.
                if (_firstFallbackPoll || !_listenerHealthy)
                {
                    _firstFallbackPoll = false;
                    await ProcessUnpublishedChangesAsync(cancellationToken);
                }

                await Task.Delay(_options.FallbackPollingInterval, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in fallback polling loop");
                try { await Task.Delay(_options.FallbackPollingInterval, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void OnNotification(object? sender, NpgsqlNotificationEventArgs e)
    {
        // Reject immediately when at capacity; the fallback poll will deliver the record.
        if (!_notificationSemaphore.Wait(0))
        {
            _logger.LogDebug(
                "Notification concurrency limit reached ({Limit}); record will be delivered by fallback poll",
                _options.MaxConcurrentNotifications);
            return;
        }

        Task.Run(async () =>
        {
            IOutbox? outbox = null;
            var claimed = false;
            var claimedId = 0L;
            // Hoisted so the catch block can emit publish-duration even on failure.
            // sw is non-null only if PublishChangeAsync was actually reached.
            Stopwatch? sw = null;
            Type? publishEntityType = null;
            EntityChange? publishChange = null;
            try
            {
                var payload = JsonSerializer.Deserialize<NotificationPayload>(e.Payload);
                if (payload == null) return;

                publishEntityType = Type.GetType(payload.EntityType);
                if (publishEntityType == null) return;

                outbox         = _publisher.GetOutbox(publishEntityType);
                var publisher  = _publisher.GetPublisher(publishEntityType);
                var serializer = _publisher.GetSerializer(publishEntityType);
                var compressor = _publisher.GetCompressor(publishEntityType);

                // Atomically claim before publishing to prevent races with the fallback
                // polling loop and OutboxPublisherService.
                if (!await outbox.TryClaimForPublishingAsync(payload.Id, _cts.Token))
                {
                    _logger.LogDebug("Change {ChangeId} for {EntityType} already claimed by another publisher, skipping",
                        payload.Id, payload.EntityType);
                    return;
                }
                claimed   = true;
                claimedId = payload.Id;

                publishChange = await GetByIdAsync(outbox, publishEntityType, payload.Id, _cts.Token);
                if (publishChange == null)
                {
                    await outbox.RevertClaimAsync(claimedId, CancellationToken.None);
                    claimed = false;
                    return;
                }

                sw = Stopwatch.StartNew();
                await PublishChangeAsync(publishChange, publishEntityType, publisher, serializer, compressor, _cts.Token);
                sw.Stop();

                // NOTIFY fast path: single attempt per notification.
                _meter.RecordPublishSuccess(publishEntityType, publishChange.ChangeType,
                    durationSeconds: sw.Elapsed.TotalSeconds,
                    lagSeconds:      (DateTime.UtcNow - publishChange.Timestamp).TotalSeconds);
                claimed = false;
            }
            catch (Exception ex)
            {
                if (sw != null && publishEntityType != null && publishChange != null)
                {
                    sw.Stop();
                    _meter.RecordPublishFailure(publishEntityType, publishChange.ChangeType, sw.Elapsed.TotalSeconds);
                }

                if (claimed && outbox != null)
                    await outbox.RevertClaimAsync(claimedId, CancellationToken.None);

                _logger.LogWarning(ex, "Error processing PostgreSQL notification");
            }
            finally
            {
                _notificationSemaphore.Release();
            }
        });
    }

    private async Task PublishChangeAsync(
        EntityChange change,
        Type entityType,
        IQueuePublisher publisher,
        IChangeSerializer serializer,
        IChangeCompressor compressor,
        CancellationToken ct)
    {
        using var serialized = new MemoryStream();
        await ((Task)SerializeMethod.MakeGenericMethod(entityType).Invoke(null, [serializer, change, serialized, ct])!);
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

        _meter.RecordPayloadSize(entityType, change.ChangeType, envelope.Payload.Length);

        await publisher.PublishAsync(envelope, ct);
    }

    private async Task ProcessUnpublishedChangesAsync(CancellationToken cancellationToken)
    {
        foreach (var (entityType, outbox) in _publisher.GetOutboxes())
        {
            if (cancellationToken.IsCancellationRequested) break;
            await ProcessSingleOutboxAsync(entityType, outbox, cancellationToken);
        }
    }

    private async Task ProcessSingleOutboxAsync(Type entityType, IOutbox outbox, CancellationToken cancellationToken)
    {
        try
        {
            var publisher  = _publisher.GetPublisher(entityType);
            var serializer = _publisher.GetSerializer(entityType);
            var compressor = _publisher.GetCompressor(entityType);
            var changes = await GetUnpublishedAsync(outbox, entityType, 100, cancellationToken);
            _meter.RecordBatchSize(entityType, changes.Count);

            await Parallel.ForEachAsync(changes,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = _options.MaxPublishConcurrency,
                    CancellationToken      = cancellationToken
                },
                async (change, token) =>
                {
                    if (!await outbox.TryClaimForPublishingAsync(change.Id, token)) return;

                    var sw = Stopwatch.StartNew();
                    try
                    {
                        await PublishChangeAsync(change, entityType, publisher, serializer, compressor, token);
                        sw.Stop();
                        // Fallback polling path: single attempt per record.
                        _meter.RecordPublishSuccess(entityType, change.ChangeType,
                            durationSeconds: sw.Elapsed.TotalSeconds,
                            lagSeconds:      (DateTime.UtcNow - change.Timestamp).TotalSeconds);
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        _meter.RecordPublishFailure(entityType, change.ChangeType, sw.Elapsed.TotalSeconds);
                        await outbox.RevertClaimAsync(change.Id, CancellationToken.None);
                        _logger.LogWarning(ex,
                            "Failed to publish change {ChangeId} for {EntityType}, reverted claim; will retry",
                            change.Id, entityType.Name);
                    }
                });

            // Successful iteration — emit recovery if this outbox was previously unhealthy.
            if (_fallbackOutboxState.TryGetValue(entityType, out var state) && state.Unhealthy
                && outbox.ConnectionComponent is { } component)
            {
                var endpoint = outbox.ConnectionEndpoint ?? "<unknown>";
                var duration = (DateTime.UtcNow - state.FirstFailureAt).TotalSeconds;
                _meter.RecordConnectionRecovery(component, endpoint, outcome: "succeeded", duration);
                _logger.LogInformation(
                    "Outbox connection recovered for {EntityType} ({Component} at {Endpoint}) after {Duration:F2}s",
                    entityType.Name, component, endpoint, duration);
                _fallbackOutboxState[entityType] = new FallbackOutboxState(Unhealthy: false, FirstFailureAt: DateTime.MinValue);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (outbox.IsConnectionFault(ex) && outbox.ConnectionComponent is not null)
        {
            var component = outbox.ConnectionComponent;
            var endpoint  = outbox.ConnectionEndpoint ?? "<unknown>";

            var state = _fallbackOutboxState.GetOrAdd(entityType,
                _ => new FallbackOutboxState(Unhealthy: false, FirstFailureAt: DateTime.MinValue));
            if (!state.Unhealthy)
            {
                _fallbackOutboxState[entityType] = new FallbackOutboxState(Unhealthy: true, FirstFailureAt: DateTime.UtcNow);
                _meter.RecordConnectionDisconnect(component, endpoint);
            }
            _logger.LogWarning(ex,
                "Outbox connection fault for {EntityType} ({Component} at {Endpoint}); fallback polling will retry",
                entityType.Name, component, endpoint);
        }
    }

    private readonly record struct FallbackOutboxState(bool Unhealthy, DateTime FirstFailureAt);

    private static Task<EntityChange?> GetByIdAsync(IOutbox outbox, Type entityType, long id, CancellationToken ct)
        => (Task<EntityChange?>)GetByIdMethod.MakeGenericMethod(entityType).Invoke(null, [outbox, id, ct])!;

    private static async Task<EntityChange?> GetByIdCoreAsync<TEntity>(IOutbox outbox, long id, CancellationToken ct)
        where TEntity : class
        => await outbox.GetByIdAsync<TEntity>(id, ct);

    private static Task<IReadOnlyList<EntityChange>> GetUnpublishedAsync(
        IOutbox outbox, Type entityType, int batchSize, CancellationToken ct)
        => (Task<IReadOnlyList<EntityChange>>)GetUnpublishedMethod
            .MakeGenericMethod(entityType)
            .Invoke(null, [outbox, batchSize, ct])!;

    private static async Task<IReadOnlyList<EntityChange>> GetUnpublishedCoreAsync<TEntity>(
        IOutbox outbox, int batchSize, CancellationToken ct)
        where TEntity : class
        => await outbox.GetUnpublishedAsync<TEntity>(batchSize, ct);

    private static Task SerializeCoreAsync<TEntity>(
        IChangeSerializer serializer,
        EntityChange<TEntity> change,
        Stream destination,
        CancellationToken ct)
        where TEntity : class
        => serializer.SerializeAsync(change, destination, ct);

    public static string GenerateNotifyTriggerFunction(string functionName, string channelName)
    {
        return $"""
                CREATE OR REPLACE FUNCTION {functionName}()
                RETURNS TRIGGER AS $$
                BEGIN
                    PERFORM pg_notify('{channelName}', json_build_object(
                        'entity_type', NEW.entity_type,
                        'id', NEW.id,
                        'change_type', NEW.change_type
                    )::text);
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """;
    }

    public static string GenerateNotifyTrigger(string triggerName, string outboxTableName, string functionName)
    {
        return $"""
                CREATE TRIGGER {triggerName}
                    AFTER INSERT ON {outboxTableName}
                    FOR EACH ROW EXECUTE FUNCTION {functionName}();
                """;
    }

    public static string GenerateDropTrigger(string triggerName, string outboxTableName)
        => $"DROP TRIGGER IF EXISTS {triggerName} ON {outboxTableName};";

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _stateGaugeSubscription?.Dispose();
        _cts.Dispose();
        _notificationSemaphore.Dispose();
        _connection?.Dispose();
    }
}
