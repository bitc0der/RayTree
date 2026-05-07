using Microsoft.Extensions.Hosting;

namespace RayTree.Subscriber;

public class ChangeSubscriberHostedService : IHostedService
{
    private readonly ChangeSubscriber _subscriber;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _consumeTasks = new();

    public ChangeSubscriberHostedService(ChangeSubscriber subscriber)
    {
        _subscriber = subscriber ?? throw new ArgumentNullException(nameof(subscriber));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var ct = _cts.Token;
        foreach (var (_, queue) in _subscriber.Queues)
        {
            await queue.InitializeAsync(cancellationToken);
            _consumeTasks.Add(Task.Run(() => _subscriber.ConsumeFromConsumerAsync(queue, ct), ct));
        }
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

        _subscriber.Dispose();
    }
}
