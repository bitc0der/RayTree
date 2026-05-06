using System.IO.Pipelines;
using System.Reflection;
using RayTree.Models;
using RayTree.Plugins;
using RayTree.Tracking;

namespace RayTree.Distribution;

public class OutboxPublisherService : IDisposable
{
    private readonly EntityChangeTracker _tracker;
    private readonly OutboxPublisherOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollingTask;
    private volatile bool _stopping;

    private static readonly MethodInfo GetUnpublishedMethod = typeof(OutboxPublisherService)
        .GetMethod(nameof(GetUnpublishedCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo SerializeMethod = typeof(OutboxPublisherService)
        .GetMethod(nameof(SerializeCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    public OutboxPublisherService(EntityChangeTracker tracker, OutboxPublisherOptions options)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _options = options ?? throw new ArgumentNullException(nameof(options));
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
                foreach (var (entityType, outbox) in _tracker.GetOutboxes())
                {
                    if (_stopping && cancellationToken.IsCancellationRequested)
                        break;

                    var publisher = _tracker.GetPublisher(entityType);
                    var serializer = _tracker.GetSerializer(entityType);
                    var compressor = _tracker.GetCompressor(entityType);

                    if (publisher == null || serializer == null || compressor == null)
                        continue;

                    var changes = await GetUnpublishedAsync(outbox, entityType, _options.BatchSize, cancellationToken);

                    foreach (var change in changes)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        await PublishWithRetryAsync(change, entityType, outbox, publisher, serializer, compressor,
                            cancellationToken);
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
        EntityChange change,
        Type entityType,
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
                await PublishChangeAsync(change, entityType, publisher, serializer, compressor, ct);
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

    private static Task<IReadOnlyList<EntityChange>> GetUnpublishedAsync(
        IOutbox outbox,
        Type entityType,
        int batchSize,
        CancellationToken ct)
    {
        return (Task<IReadOnlyList<EntityChange>>)GetUnpublishedMethod
            .MakeGenericMethod(entityType)
            .Invoke(null, [outbox, batchSize, ct])!;
    }

    private static async Task<IReadOnlyList<EntityChange>> GetUnpublishedCoreAsync<TEntity>(
        IOutbox outbox,
        int batchSize,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        return await outbox.GetUnpublishedAsync<TEntity>(batchSize, cancellationToken);
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

        var serializeTask = (Task)SerializeMethod.MakeGenericMethod(entityType)
            .Invoke(null, [serializer, change, serializePipe.Writer, ct])!;
        var compressTask = compressor.CompressAsync(serializePipe.Reader, compressPipe.Writer, ct);
        var publishTask = publisher.PublishAsync(change, compressPipe.Reader, ct);

        await Task.WhenAll(serializeTask, compressTask, publishTask);
    }

    private static Task SerializeCoreAsync<TEntity>(
        IChangeSerializer serializer,
        EntityChange<TEntity> change,
        PipeWriter writer,
        CancellationToken ct)
        where TEntity : class
    {
        return serializer.SerializeAsync(change, writer, ct);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
