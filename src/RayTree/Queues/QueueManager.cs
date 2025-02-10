using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace RayTree.Queues;

internal sealed class QueueManager : IAsyncDisposable
{
	private readonly IQueueProvider _provider;

	private readonly ConcurrentDictionary<string, IQueue> _queues = new();

	public QueueManager(IQueueProvider provider)
	{
		_provider = provider ?? throw new ArgumentNullException(nameof(provider));
	}

	public IQueue GetOrCreate(string queueName)
	{
		return queueName is null
			? throw new ArgumentNullException(nameof(queueName))
			: _queues.GetOrAdd(queueName, Create);
	}

	private IQueue Create(string queueName) => _provider.Create(queueName);

	public void Start()
	{
		foreach (var queue in _queues.Values)
		{
			queue.Start();
		}
	}

	public async Task StopAsync()
	{
		foreach (var queue in _queues.Values)
		{
			await queue.StopAsync();
		}
	}

	public async ValueTask DisposeAsync()
	{
		foreach(var queue in _queues.Values)
		{
			if (queue is IAsyncDisposable asyncDisposale)
			{
				await asyncDisposale.DisposeAsync();
			}
			else if (queue is IDisposable disposable)
			{
				disposable.Dispose();
			}
		}
	}
}