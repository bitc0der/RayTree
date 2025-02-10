using System;
using System.Threading.Tasks;
using System.Threading;

namespace RayTree.Queues.InMemory;

internal sealed class InMemoryQueue : IQueue
{
	private readonly AwaitableQueue<object> _queue = new();
	private readonly BackgroudJob _job = new();

	private IQueue.HandleMessage? _handlers;

	public string Name { get; }

	public InMemoryQueue(string name)
	{
		Name = name ?? throw new ArgumentNullException(nameof(name));
	}

	public void Send<TMessage>(TMessage message)
		where TMessage : class
	{
		ArgumentNullException.ThrowIfNull(message);

		_queue.Add(message);
	}

	public void Subscribe(IQueue.HandleMessage handleMessage)
	{
		_handlers += handleMessage ?? throw new ArgumentNullException(nameof(handleMessage));
	}

	public void Unsubscribe(IQueue.HandleMessage handleMessage)
	{
		_handlers -= handleMessage ?? throw new ArgumentNullException( nameof(handleMessage));
	}

	public void Start(CancellationToken cancellationToken) => _job.Start(DoRoutineAsync);

	private async Task DoRoutineAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			var message = await _queue.GetAsync(cancellationToken);

			var handlers = _handlers;

			if (handlers is null)
				continue;

			handlers(message);
		}
	}

	public Task StopAsync() => _job.StopAsync();
}