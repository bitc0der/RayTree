using RayTree.Plugins;
using RayTree.Tracking;

namespace RayTree.Distribution;

public class OutboxPublisherOptions
{
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int BatchSize { get; set; } = 100;
    public bool UseNotificationChannel { get; set; }
    public string? NotificationChannel { get; set; }
    public TimeSpan? FallbackPollingInterval { get; set; }
    public int MaxRetryCount { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
}

public class OutboxPublisherService : IDisposable
{
    private readonly EntityChangeTracker _tracker;
    private readonly OutboxPublisherOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollingTask;
    private volatile bool _stopping;

    public OutboxPublisherService(EntityChangeTracker tracker, OutboxPublisherOptions options)
    {
        _tracker = tracker;
        _options = options;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _pollingTask = PollAndPublishAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _stopping = true;
        _cts.Cancel();

        if (_pollingTask != null)
        {
            await Task.WhenAny(_pollingTask, Task.Delay(30000, cancellationToken));
        }
    }

    private async Task PollAndPublishAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                foreach (var entityType in GetTrackedEntityTypes())
                {
                    if (_stopping && cancellationToken.IsCancellationRequested)
                        break;

                    var outbox = _tracker.GetOutbox(entityType);
                    var publisher = _tracker.GetPublisher(entityType);
                    var serializer = _tracker.GetSerializer(entityType);
                    var compressor = _tracker.GetCompressor(entityType);

                    if (outbox == null || publisher == null || serializer == null || compressor == null)
                        continue;

                    var changes = await outbox.GetUnpublishedAsync(_options.BatchSize, cancellationToken);

                    foreach (var change in changes)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        await PublishWithRetryAsync(change, outbox, publisher, serializer, compressor, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                await Task.Delay(_options.PollingInterval, cancellationToken);
            }

            await Task.Delay(_options.PollingInterval, cancellationToken);
        }
    }

    private async Task PublishWithRetryAsync(
        Models.EntityChange change,
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
            catch (Exception)
            {
                retries++;
                if (retries >= _options.MaxRetryCount)
                    throw;

                await Task.Delay(_options.RetryDelay * retries, ct);
            }
        }
    }

    private IEnumerable<Type> GetTrackedEntityTypes()
    {
        return _tracker.GetOutboxes().Keys;
    }

    private static async Task PublishChangeAsync(
        Models.EntityChange change,
        IQueuePublisher publisher,
        IChangeSerializer serializer,
        IChangeCompressor compressor,
        CancellationToken ct)
    {
        var serializePipe = new System.IO.Pipelines.Pipe();
        var compressPipe = new System.IO.Pipelines.Pipe();

        var serializeTask = serializer.SerializeAsync(change, serializePipe.Writer, ct);
        var compressTask = compressor.CompressAsync(serializePipe.Reader, compressPipe.Writer, ct);
        var publishTask = publisher.PublishAsync(change, compressPipe.Reader, ct);

        await Task.WhenAll(serializeTask, compressTask, publishTask);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
