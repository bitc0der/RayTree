using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace RayTree.Queues;

internal sealed class QueueManager
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

	public async Task Stop()
	{
		foreach (var queue in _queues.Values)
		{
			await queue.StopAsync();
		}
	}
}