using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RayTree.Core.Tracking;

namespace RayTree.Hosting;

public class ChangeTrackingHostedService : IHostedService
{
    private readonly EntityChangeTracker _tracker;
    private readonly ILogger<ChangeTrackingHostedService> _logger;
    private readonly ChangeTrackingDiContext? _diContext;
    private readonly CancellationTokenSource _cts = new();

    public ChangeTrackingHostedService(
        EntityChangeTracker tracker,
        ILogger<ChangeTrackingHostedService> logger,
        ChangeTrackingDiContext? diContext)
    {
        _tracker   = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _logger    = logger  ?? throw new ArgumentNullException(nameof(logger));
        _diContext = diContext;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_diContext is not null && _logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "ChangeTracking starting. ConfigurationBound={ConfigurationBound}",
                _diContext.ConfigurationBound);

        return _tracker.StartAsync(_cts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        await _tracker.StopAsync();
        _logger.LogInformation("Change tracking hosted service stopped");
    }
}
