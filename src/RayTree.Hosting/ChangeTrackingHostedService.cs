using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RayTree.Core.Tracking;

namespace RayTree.Hosting;

public class ChangeTrackingHostedService : IHostedService
{
    private readonly EntityChangeTracker _tracker;
    private readonly ILogger<ChangeTrackingHostedService> _logger;
    private readonly CancellationTokenSource _cts = new();

    public ChangeTrackingHostedService(
        EntityChangeTracker tracker,
        ILogger<ChangeTrackingHostedService> logger)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _logger  = logger  ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
        => _tracker.StartAsync(_cts.Token);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        await _tracker.StopAsync();
        _logger.LogInformation("Change tracking hosted service stopped");
    }
}
