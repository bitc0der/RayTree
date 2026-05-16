using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RayTree.Core.Tracking;

namespace RayTree.Hosting;

/// <summary>
/// Unified hosted service that drives consumer loops for all entity types registered on the
/// tracker. Publisher loops are started during <see cref="EntityChangeTracker.InitializeAsync"/>
/// (called inside <see cref="ChangeTrackingBuilder.Build"/>), so this service only manages
/// the subscriber side.
///
/// <para>Starts one consume loop per entity in <em>Shared</em> mode (existing behavior) and
/// one consume loop per <c>(entity type, handler name)</c> pair in <em>Isolated</em> mode.</para>
/// </summary>
public class ChangeTrackingHostedService : IHostedService
{
    private readonly EntityChangeTracker _tracker;
    private readonly ILogger<ChangeTrackingHostedService> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _consumeTasks = new();

    public ChangeTrackingHostedService(
        EntityChangeTracker tracker,
        ILogger<ChangeTrackingHostedService> logger)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _logger  = logger  ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var ct = _cts.Token;
        if (_tracker.Subscriber is not { } subscriber)
            return Task.CompletedTask;

        // Task 4.1 — Shared-mode: one loop per entity (existing behavior)
        var sharedQueues = subscriber.Queues;
        var sharedTotal  = sharedQueues.Count;
        var index        = 0;
        foreach (var (entityType, consumer) in sharedQueues)
        {
            index++;
            _logger.LogInformation(
                "Starting Shared-mode consumer loop for {EntityType} ({Index} of {Total})",
                entityType.Name, index, sharedTotal);
            _consumeTasks.Add(Task.Run(
                () => _tracker.ConsumeFromConsumerAsync(consumer, ct), ct));
        }

        // Task 4.1 — Isolated-mode: one loop per (entity, handlerName)
        // Task 4.3 — Information-level logging per started loop
        var isolatedQueues = subscriber.IsolatedQueues;
        foreach (var ((entityType, handlerName), consumer) in isolatedQueues)
        {
            _logger.LogInformation(
                "Starting Isolated-mode consumer loop for {EntityType}/{HandlerName}",
                entityType.Name, handlerName);
            // Capture loop variables
            var capturedEntityType  = entityType;
            var capturedHandlerName = handlerName;
            var capturedConsumer    = consumer;
            _consumeTasks.Add(Task.Run(
                () => subscriber.ConsumeIsolatedFromConsumerAsync(
                    capturedConsumer, capturedEntityType, capturedHandlerName, ct),
                ct));
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();

        try
        {
            await Task.WhenAll(_consumeTasks);
        }
        catch (OperationCanceledException)
        {
            // expected on graceful shutdown
        }

        // Task 4.3 — Information log on graceful shutdown
        _logger.LogInformation("Change tracking hosted service stopped");
    }
}
