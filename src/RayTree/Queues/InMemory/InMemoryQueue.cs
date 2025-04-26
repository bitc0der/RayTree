using System;
using System.Threading.Tasks;
using System.Threading;

namespace RayTree.Queues.InMemory;

internal sealed class InMemoryQueue : IQueue
{
	private readonly AwaitableQueue<object> _queue = new();

	public string Name { get; }

	public InMemoryQueue(string name)
	{
		Name = name ?? throw new ArgumentNullException(nameof(name));
	}

	public Task SendAsync<TMessage>(TMessage message, CancellationToken cancellationToken)
		where TMessage : class
	{
		ArgumentNullException.ThrowIfNull(message);

		_queue.Add(message);

		return Task.CompletedTask;
	}

	public void Start() { }

	public Task StopAsync() => Task.CompletedTask;

	public Task<object> ReadMessageAsync(CancellationToken cancellationToken)
	{
		return _queue.GetAsync(cancellationToken);
	}
}