using System;

namespace RayTree.Queues;

internal sealed class QueueManager
{
	private readonly IQueueProvider _provider;

	public QueueManager(IQueueProvider provider)
	{
		_provider = provider ?? throw new ArgumentNullException(nameof(provider));
	}
}