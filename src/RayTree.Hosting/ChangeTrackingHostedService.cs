using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RayTree.Core.Tracking;

namespace RayTree.Hosting;

/// <summary>
/// Unified hosted service that drives consumer loops for all entity types registered on the
/// tracker. Publisher loops are started during <see cref="EntityChangeTracker.InitializeAsync"/>
/// (called inside <see cref="ChangeTrackingBuilder.Build"/>), so this service only manages
/// the subscriber side.
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
        _logger  = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var ct = _cts.Token;
        if (_tracker.Subscriber is { } subscriber)
        {
            var queues = subscriber.Queues;
            var total  = queues.Count;
            var index  = 0;
            foreach (var (_, consumer) in queues)
            {
                index++;
                _logger.LogInformation(
                    "Starting change tracking consumer loop {Index} of {Total}",
                    index, total);
                _consumeTasks.Add(Task.Run(() => _tracker.ConsumeFromConsumerAsync(consumer, ct), ct));
            }
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

        _logger.LogInformation("Change tracking hosted service stopped");
    }
}
