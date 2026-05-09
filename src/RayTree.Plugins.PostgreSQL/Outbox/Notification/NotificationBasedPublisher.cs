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
    private NpgsqlConnection? _connection;
    private Task? _listenTask;
    private Task? _fallbackTask;

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
        _publisher = publisher    ?? throw new ArgumentNullException(nameof(publisher));
        _options   = options      ?? throw new ArgumentNullException(nameof(options));
        _logger    = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
                         .CreateLogger<NotificationBasedPublisher>();
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
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error waiting for PostgreSQL notifications on {ChannelName}, falling back to polling",
                    _options.ChannelName);
                await Task.Delay(_options.FallbackPollingInterval, cancellationToken);
            }
        }
    }

    private async Task FallbackPollingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ProcessUnpublishedChangesAsync(cancellationToken);
                await Task.Delay(_options.FallbackPollingInterval, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in fallback polling loop");
                try { await Task.Delay(_options.FallbackPollingInterval, cancellationToken); }
                catch { }
            }
        }
    }

    private void OnNotification(object? sender, NpgsqlNotificationEventArgs e)
    {
        Task.Run(async () =>
        {
            try
            {
                var payload = JsonSerializer.Deserialize<NotificationPayload>(e.Payload);
                if (payload == null) return;

                var entityType = Type.GetType(payload.EntityType);
                if (entityType == null) return;

                var outbox     = _publisher.GetOutbox(entityType);
                var publisher  = _publisher.GetPublisher(entityType);
                var serializer = _publisher.GetSerializer(entityType);
                var compressor = _publisher.GetCompressor(entityType);

                var change = await GetByIdAsync(outbox, entityType, payload.Id, _cts.Token);
                if (change == null || change.Published) return;

                await PublishChangeAsync(change, entityType, publisher, serializer, compressor, _cts.Token);
                await outbox.MarkPublishedAsync(change.Id, _cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing PostgreSQL notification");
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

            foreach (var change in changes)
            {
                if (cancellationToken.IsCancellationRequested) break;
                try
                {
                    await PublishChangeAsync(change, entityType, publisher, serializer, compressor, cancellationToken);
                    await outbox.MarkPublishedAsync(change.Id, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to publish change {ChangeId} for {EntityType}, will retry in next polling cycle",
                        change.Id, entityType.Name);
                }
            }
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
        _connection?.Dispose();
    }
}
