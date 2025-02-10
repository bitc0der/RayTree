using System;
using System.Collections.Concurrent;

namespace RayTree.Queues;

internal sealed class QueueManager
{
	private readonly IQueueProvider _provider;

	private readonly ConcurrentDictionary<string, IQueue> _queues = new();

	public QueueManager(IQueueProvider provider)
	{
		_provider = provider ?? throw new ArgumentNullException(nameof(provider));
	}

	public IQueue GetOrCreate(string id)
	{
		return id is null
			? throw new ArgumentNullException(nameof(id))
			: _queues.GetOrAdd(id, Create);
	}

	private IQueue Create(string id) => _provider.Create();
}