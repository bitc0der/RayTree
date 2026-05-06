using System.IO.Pipelines;
using System.Reflection;
using RayTree.Models;
using RayTree.Plugins;
using RayTree.Tracking;

namespace RayTree.Distribution;

public class OutboxPublisherService : IDisposable
{
    private readonly EntityChangeTracker _tracker;
    private readonly Type _entityType;
    private readonly OutboxPublisherOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollingTask;
    private volatile bool _stopping;

    private static readonly MethodInfo GetUnpublishedMethod = typeof(OutboxPublisherService)
        .GetMethod(nameof(GetUnpublishedCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo SerializeMethod = typeof(OutboxPublisherService)
        .GetMethod(nameof(SerializeCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    public OutboxPublisherService(EntityChangeTracker tracker, Type entityType, OutboxPublisherOptions options)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _entityType = entityType ?? throw new ArgumentNullException(nameof(entityType));
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
            await Task.WhenAny(_pollingTask, Task.Delay(30000, cancellationToken));
    }

    private async Task PollAndPublishAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!_stopping)
                    await ProcessBatchAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // swallow and retry after interval
            }

            try
            {
                await Task.Delay(_options.PollingInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var outbox = _tracker.GetOutbox(_entityType);
        var publisher = _tracker.GetPublisher(_entityType);
        var serializer = _tracker.GetSerializer(_entityType);
        var compressor = _tracker.GetCompressor(_entityType);

        var changes = await GetUnpublishedAsync(outbox, _options.BatchSize, cancellationToken);

        foreach (var change in changes)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await PublishWithRetryAsync(change, outbox, publisher, serializer, compressor, cancellationToken);
        }
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
            catch (Exception)
            {
                retries++;
                if (retries >= _options.MaxRetryCount)
                    throw;

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
    {
        return await outbox.GetUnpublishedAsync<TEntity>(batchSize, cancellationToken);
    }

    private Task PublishChangeAsync(
        EntityChange change,
        IQueuePublisher publisher,
        IChangeSerializer serializer,
        IChangeCompressor compressor,
        CancellationToken ct)
    {
        var serializePipe = new Pipe();
        var compressPipe = new Pipe();

        var serializeTask = (Task)SerializeMethod.MakeGenericMethod(_entityType)
            .Invoke(null, [serializer, change, serializePipe.Writer, ct])!;
        var compressTask = compressor.CompressAsync(serializePipe.Reader, compressPipe.Writer, ct);
        var publishTask = publisher.PublishAsync(change, compressPipe.Reader, ct);

        return Task.WhenAll(serializeTask, compressTask, publishTask);
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
