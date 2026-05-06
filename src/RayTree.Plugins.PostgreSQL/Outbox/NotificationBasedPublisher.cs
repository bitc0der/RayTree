using System.IO.Pipelines;
using System.Reflection;
using System.Text.Json;
using Npgsql;
using RayTree.Core.Tracking;
using RayTree.Models;
using RayTree.Plugins;
using RayTree.Tracking;

namespace RayTree.Distribution;

public class NotificationBasedPublisher : IDisposable
{
    private readonly EntityChangeTracker _tracker;
    private readonly NotificationBasedPublisherOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private NpgsqlConnection? _connection;
    private Task? _listenTask;
    private Task? _fallbackTask;

    private static readonly MethodInfo _getByIdMethod = typeof(NotificationBasedPublisher)
        .GetMethod(nameof(GetByIdCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo _getUnpublishedMethod = typeof(NotificationBasedPublisher)
        .GetMethod(nameof(GetUnpublishedCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo _serializeMethod = typeof(NotificationBasedPublisher)
        .GetMethod(nameof(SerializeCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    public NotificationBasedPublisher(EntityChangeTracker tracker, NotificationBasedPublisherOptions options)
    {
        _tracker = tracker;
        _options = options;
    }

    public bool IsRunning => _listenTask != null && !_listenTask.IsCompleted;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _connection = new NpgsqlConnection(_options.ConnectionString);
        await _connection.OpenAsync(cancellationToken);

        _connection.Notification += OnNotification;

        await using var cmd = new NpgsqlCommand($"LISTEN {_options.ChannelName}", _connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        _listenTask = ListenLoopAsync(_cts.Token);
        _fallbackTask = FallbackPollingLoopAsync(_cts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cts.Cancel();

        if (_listenTask != null)
        {
            await Task.WhenAny(_listenTask, Task.Delay(5000, cancellationToken));
        }

        if (_fallbackTask != null)
        {
            await Task.WhenAny(_fallbackTask, Task.Delay(5000, cancellationToken));
        }

        if (_connection != null)
        {
            try
            {
                await using var cmd = new NpgsqlCommand($"UNLISTEN {_options.ChannelName}", _connection);
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch
            {
            }

            await _connection.CloseAsync();
        }
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _connection!.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
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
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                try
                {
                    await Task.Delay(_options.FallbackPollingInterval, cancellationToken);
                }
                catch
                {
                }
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

                var outbox = _tracker.GetOutbox(entityType);
                var publisher = _tracker.GetPublisher(entityType);
                var serializer = _tracker.GetSerializer(entityType);
                var compressor = _tracker.GetCompressor(entityType);

                var change = await GetByIdAsync(outbox, entityType, payload.Id, _cts.Token);
                if (change == null || change.Published) return;

                await PublishChangeAsync(change, entityType, publisher, serializer, compressor, _cts.Token);
                await outbox.MarkPublishedAsync(change.Id, _cts.Token);
            }
            catch (Exception)
            {
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
        var serializePipe = new Pipe();
        var compressPipe = new Pipe();

        var serializeTask = (Task)_serializeMethod.MakeGenericMethod(entityType)
            .Invoke(null, [serializer, change, serializePipe.Writer, ct])!;
        var compressTask = compressor.CompressAsync(serializePipe.Reader, compressPipe.Writer, ct);
        var publishTask = publisher.PublishAsync(change, compressPipe.Reader, ct);

        await Task.WhenAll(serializeTask, compressTask, publishTask);
    }

    private async Task ProcessUnpublishedChangesAsync(CancellationToken cancellationToken)
    {
        foreach (var (entityType, outbox) in _tracker.GetOutboxes())
        {
            if (cancellationToken.IsCancellationRequested) break;

            var publisher = _tracker.GetPublisher(entityType);
            var serializer = _tracker.GetSerializer(entityType);
            var compressor = _tracker.GetCompressor(entityType);
            var changes = await GetUnpublishedAsync(outbox, entityType, 100, cancellationToken);

            foreach (var change in changes)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    await PublishChangeAsync(change, entityType, publisher, serializer, compressor, cancellationToken);
                    await outbox.MarkPublishedAsync(change.Id, cancellationToken);
                }
                catch
                {
                }
            }
        }
    }

    private static Task<EntityChange?> GetByIdAsync(IOutbox outbox, Type entityType, long id, CancellationToken ct)
        => (Task<EntityChange?>)_getByIdMethod.MakeGenericMethod(entityType).Invoke(null, [outbox, id, ct])!;

    private static async Task<EntityChange?> GetByIdCoreAsync<TEntity>(IOutbox outbox, long id, CancellationToken ct)
        where TEntity : class
    {
        return await outbox.GetByIdAsync<TEntity>(id, ct);
    }

    private static Task<IReadOnlyList<EntityChange>> GetUnpublishedAsync(
        IOutbox outbox,
        Type entityType,
        int batchSize,
        CancellationToken ct)
    {
        return (Task<IReadOnlyList<EntityChange>>)_getUnpublishedMethod
            .MakeGenericMethod(entityType)
            .Invoke(null, [outbox, batchSize, ct])!;
    }

    private static async Task<IReadOnlyList<EntityChange>> GetUnpublishedCoreAsync<TEntity>(
        IOutbox outbox,
        int batchSize,
        CancellationToken ct)
        where TEntity : class
    {
        return await outbox.GetUnpublishedAsync<TEntity>(batchSize, ct);
    }

    private static Task SerializeCoreAsync<TEntity>(
        IChangeSerializer serializer,
        EntityChange<TEntity> change,
        PipeWriter writer, CancellationToken ct)
        where TEntity : class
    {
        return serializer.SerializeAsync(change, writer, ct);
    }

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
    {
        return $"DROP TRIGGER IF EXISTS {triggerName} ON {outboxTableName};";
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _cts.Dispose();
        _connection?.Dispose();
    }
}
