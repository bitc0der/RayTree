using System;
using System.Collections.Concurrent;
using RayTree.Handlers;
using RayTree.Queues;

namespace RayTree.Routing;

internal sealed class MessageRouter
{
	private readonly QueueManager _queueManager;

	private readonly ConcurrentBag<MessageHandlerWrapper> _handlers = [];

	public MessageRouter(QueueManager queueManager)
	{
		_queueManager = queueManager ?? throw new ArgumentNullException(nameof(queueManager));
	}

	public void Register(IMessageHandler handler)
	{
		ArgumentNullException.ThrowIfNull(handler);

		var id = handler.Id;

		var queue = _queueManager.GetOrCreate(id);

		var wrapper = new MessageHandlerWrapper(inputQueue: queue, handler: handler);

		_handlers.Add(wrapper);
	}

	public void Route<TMessage>(INode source, TMessage message)
		where TMessage : class
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(message);

		throw new NotImplementedException();
	}
}