using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using RayTree.Core.Distribution;
using RayTree.Core.Models;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Serialization;

namespace RayTree.Plugins.PostgreSQL.Outbox.Notification;

public class NotificationBasedPublisher : IDisposable
{
    private readonly ChangePublisher _publisher;
    private readonly NotificationBasedPublisherOptions _options;
    private readonly ILogger<NotificationBasedPublisher> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _notificationSemaphore;
    private NpgsqlConnection? _connection;
    private Task? _listenTask;
    private Task? _fallbackTask;
    private volatile bool _listenerHealthy = true;
    private bool _firstFallbackPoll = true;

    private static readonly MethodInfo GetByIdMethod = typeof(NotificationBasedPublisher)
        .GetMethod(nameof(GetByIdCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo GetUnpublishedMethod = typeof(NotificationBasedPublisher)
        .GetMethod(nameof(GetUnpublishedCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo SerializeMethod = typeof(NotificationBasedPublisher)
        .GetMethod(nameof(SerializeCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    public NotificationBasedPublisher(
        ChangePublisher publisher,
        NotificationBasedPublisherOptions options,
        ILoggerFactory loggerFactory)
    {
        _publisher            = publisher    ?? throw new ArgumentNullException(nameof(publisher));
        _options              = options      ?? throw new ArgumentNullException(nameof(options));
        _logger               = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
                                    .CreateLogger<NotificationBasedPublisher>();
        _notificationSemaphore = new SemaphoreSlim(options.MaxConcurrentNotifications,
                                                   options.MaxConcurrentNotifications);
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
            catch (Exception ex)
            {
                if (_listenerHealthy)
                {
                    _listenerHealthy = false;
                    _logger.LogWarning(ex, "PostgreSQL LISTEN connection on {ChannelName} lost, falling back to polling",
                        _options.ChannelName);
                }
                try { await Task.Delay(_options.FallbackPollingInterval, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

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
            try
            {
                var payload = JsonSerializer.Deserialize<NotificationPayload>(e.Payload);
                if (payload == null) return;

                var entityType = Type.GetType(payload.EntityType);
                if (entityType == null) return;

                outbox         = _publisher.GetOutbox(entityType);
                var publisher  = _publisher.GetPublisher(entityType);
                var serializer = _publisher.GetSerializer(entityType);
                var compressor = _publisher.GetCompressor(entityType);

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

                var change = await GetByIdAsync(outbox, entityType, payload.Id, _cts.Token);
                if (change == null)
                {
                    await outbox.RevertClaimAsync(claimedId, CancellationToken.None);
                    claimed = false;
                    return;
                }

                await PublishChangeAsync(change, entityType, publisher, serializer, compressor, _cts.Token);
                claimed = false;
            }
            catch (Exception ex)
            {
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

    private static async Task PublishChangeAsync(
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

        await publisher.PublishAsync(envelope, ct);
    }

    private async Task ProcessUnpublishedChangesAsync(CancellationToken cancellationToken)
    {
        foreach (var (entityType, outbox) in _publisher.GetOutboxes())
        {
            if (cancellationToken.IsCancellationRequested) break;

            var publisher  = _publisher.GetPublisher(entityType);
            var serializer = _publisher.GetSerializer(entityType);
            var compressor = _publisher.GetCompressor(entityType);
            var changes    = await GetUnpublishedAsync(outbox, entityType, 100, cancellationToken);

            await Parallel.ForEachAsync(changes,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = _options.MaxPublishConcurrency,
                    CancellationToken      = cancellationToken
                },
                async (change, token) =>
                {
                    if (!await outbox.TryClaimForPublishingAsync(change.Id, token)) return;
                    try
                    {
                        await PublishChangeAsync(change, entityType, publisher, serializer, compressor, token);
                    }
                    catch (Exception ex)
                    {
                        await outbox.RevertClaimAsync(change.Id, CancellationToken.None);
                        _logger.LogWarning(ex,
                            "Failed to publish change {ChangeId} for {EntityType}, reverted claim; will retry",
                            change.Id, entityType.Name);
                    }
                });
        }
    }

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
        _cts.Dispose();
        _notificationSemaphore.Dispose();
        _connection?.Dispose();
    }
}
