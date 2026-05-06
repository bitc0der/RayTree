using Microsoft.Extensions.Hosting;
using RayTree.Core.Distribution;
using RayTree.Core.Tracking;

namespace RayTree.Hosting;

public class OutboxPublisherHostedService : IHostedService
{
    private readonly EntityChangeTracker _tracker;
    private readonly OutboxPublisherOptions _options;
    private readonly OutboxCleanupService? _cleanupService;
    private readonly List<OutboxPublisherService> _publisherServices = new();

    public OutboxPublisherHostedService(
        EntityChangeTracker tracker,
        OutboxPublisherOptions options,
        OutboxCleanupService? cleanupService = null)
    {
        _tracker = tracker;
        _options = options;
        _cleanupService = cleanupService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var entityType in _tracker.GetOutboxes().Keys)
        {
            var service = new OutboxPublisherService(_tracker, entityType, _options);
            _publisherServices.Add(service);
            await service.StartAsync(cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var service in _publisherServices)
            await service.StopAsync(cancellationToken);
    }
}
