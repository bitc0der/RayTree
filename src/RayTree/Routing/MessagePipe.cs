using RayTree.Queues;
using System;

namespace RayTree.Routing;

internal sealed class MessagePipe
{
	private readonly IQueue _outputQueue;

	public string Id { get; }

	public MessagePipe(string id, QueueManager queueManager)
	{
		Id = id;
		_outputQueue = queueManager.GetOrCreate(queueName: id); // TODO: build queue name with node and pipe Ids
	}

	public void Send(object message)
	{
		ArgumentNullException.ThrowIfNull(message);

		_outputQueue.Send(message);
	}
}