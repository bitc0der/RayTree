using Microsoft.Extensions.Hosting;
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
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _consumeTasks = new();

    public ChangeTrackingHostedService(EntityChangeTracker tracker)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var ct = _cts.Token;
        foreach (var (_, consumer) in _tracker.Subscriber!.Queues)
            _consumeTasks.Add(Task.Run(() => _tracker.ConsumeFromConsumerAsync(consumer, ct), ct));

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
    }
}
