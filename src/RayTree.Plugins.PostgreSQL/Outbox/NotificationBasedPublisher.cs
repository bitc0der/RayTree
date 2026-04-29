using System.IO.Pipelines;
using System.Text.Json;
using Npgsql;
using RayTree.Models;
using RayTree.Plugins;
using RayTree.Tracking;

namespace RayTree.Distribution;

public class NotificationBasedPublisherOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string ChannelName { get; set; } = "entity_changes";
    public TimeSpan FallbackPollingInterval { get; set; } = TimeSpan.FromSeconds(30);
}

public class NotificationBasedPublisher : IDisposable
{
    private readonly EntityChangeTracker _tracker;
    private readonly NotificationBasedPublisherOptions _options;
    private NpgsqlConnection? _connection;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;

    public NotificationBasedPublisher(EntityChangeTracker tracker, NotificationBasedPublisherOptions options)
    {
        _tracker = tracker;
        _options = options;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _connection = new NpgsqlConnection(_options.ConnectionString);
        await _connection.OpenAsync(cancellationToken);

        _connection.Notification += OnNotification;

        using var cmd = new NpgsqlCommand($"LISTEN {_options.ChannelName}", _connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        _listenTask = WaitLoopAsync(_cts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cts.Cancel();

        if (_listenTask != null)
        {
            await Task.WhenAny(_listenTask, Task.Delay(5000, cancellationToken));
        }

        if (_connection != null)
        {
            await using var cmd = new NpgsqlCommand($"UNLISTEN {_options.ChannelName}", _connection);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await _connection.CloseAsync();
        }
    }

    private async Task WaitLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _connection?.Wait();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(_options.FallbackPollingInterval, cancellationToken);
            }
        }
    }

    private async void OnNotification(object? sender, NpgsqlNotificationEventArgs e)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<NotificationPayload>(e.Payload);
            if (payload == null)
                return;

            var entityType = Type.GetType(payload.EntityType);
            if (entityType == null)
                return;

            var outbox = _tracker.GetOutbox(entityType);
            var publisher = _tracker.GetPublisher(entityType);
            var serializer = _tracker.GetSerializer(entityType);
            var compressor = _tracker.GetCompressor(entityType);

            if (outbox == null || publisher == null || serializer == null || compressor == null)
                return;

            var change = await outbox.GetByIdAsync(payload.Id, _cts.Token);
            if (change == null || change.Published)
                return;

            await PublishChangeAsync(change, publisher, serializer, compressor, _cts.Token);
            await outbox.MarkPublishedAsync(change.Id, _cts.Token);
        }
        catch (Exception)
        {
        }
    }

    private static async Task PublishChangeAsync(
        EntityChange change,
        IQueuePublisher publisher,
        IChangeSerializer serializer,
        IChangeCompressor compressor,
        CancellationToken ct)
    {
        var serializePipe = new Pipe();
        var compressPipe = new Pipe();

        var serializeTask = serializer.SerializeAsync(change, serializePipe.Writer, ct);
        var compressTask = compressor.CompressAsync(serializePipe.Reader, compressPipe.Writer, ct);
        var publishTask = publisher.PublishAsync(change, compressPipe.Reader, ct);

        await Task.WhenAll(serializeTask, compressTask, publishTask);
    }

    public static string GenerateNotifyTriggerFunction(string outboxTableName, string functionName)
    {
        return $"""
            CREATE OR REPLACE FUNCTION {functionName}()
            RETURNS TRIGGER AS $$
            BEGIN
                PERFORM pg_notify('entity_changes', json_build_object(
                    'entity', NEW.entity_type,
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
        _cts.Cancel();
        _cts.Dispose();
        _connection?.Dispose();
    }
}

public class NotificationPayload
{
    public string EntityType { get; set; } = string.Empty;
    public long Id { get; set; }
    public string ChangeType { get; set; } = string.Empty;
}
