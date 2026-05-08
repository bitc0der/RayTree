using Microsoft.Extensions.Hosting;
using RayTree.Core.Distribution;

namespace RayTree.Hosting.Publishing;

public class OutboxPublisherHostedService : IHostedService
{
    private readonly ChangePublisher _publisher;
    private readonly OutboxPublisherOptions _options;
    private readonly OutboxCleanupService? _cleanupService;
    private readonly List<OutboxPublisherService> _publisherServices = new();

    public OutboxPublisherHostedService(
        ChangePublisher publisher,
        OutboxPublisherOptions options,
        OutboxCleanupService? cleanupService = null)
    {
        _publisher = publisher;
        _options = options;
        _cleanupService = cleanupService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var entityType in _publisher.GetOutboxes().Keys)
        {
            var service = new OutboxPublisherService(_publisher, entityType, _options);
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
