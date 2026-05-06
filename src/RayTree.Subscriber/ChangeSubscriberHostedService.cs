using Microsoft.Extensions.Hosting;

namespace RayTree.Subscriber;

public class ChangeSubscriberHostedService : IHostedService
{
    private readonly ChangeSubscriber _subscriber;
    private readonly CancellationTokenSource _cts = new();

    public ChangeSubscriberHostedService(ChangeSubscriber subscriber)
    {
        _subscriber = subscriber ?? throw new ArgumentNullException(nameof(subscriber));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        _subscriber.Dispose();
    }
}
