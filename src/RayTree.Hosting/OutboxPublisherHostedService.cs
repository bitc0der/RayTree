using Microsoft.Extensions.Hosting;
using RayTree.Distribution;
using RayTree.Tracking;

namespace RayTree.Hosting;

public class OutboxPublisherHostedService : IHostedService
{
    private readonly OutboxPublisherService _publisher;
    private readonly OutboxCleanupService? _cleanupService;
    private readonly CancellationTokenSource _cts = new();

    public OutboxPublisherHostedService(
        EntityChangeTracker tracker,
        OutboxPublisherOptions options,
        OutboxCleanupService? cleanupService = null)
    {
        _publisher = new OutboxPublisherService(tracker, options);
        _cleanupService = cleanupService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = _publisher.StartAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _publisher.StopAsync(cancellationToken);
        _cts.Cancel();
    }
}
