using System;
using System.Collections.Concurrent;
using RayTree.Handlers;
using RayTree.Queues;

namespace RayTree.Routing;

internal sealed class MessageRouter
{
	private readonly QueueManager _queueManager;

	private readonly ConcurrentBag<MessageHandlerWrapper> _handlers = [];
	private readonly ConcurrentDictionary<string, MessagePipe> _pipes = [];

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

	public void Route<TMessage>(INode source, string pipeName, TMessage message)
		where TMessage : class
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(message);

		var pipe = _pipes.GetOrAdd(pipeName, CreatePipe);

		pipe.Send(message);
	}

	private MessagePipe CreatePipe(string id) => id is null
		? throw new ArgumentNullException(nameof(id))
		: new MessagePipe(id, _queueManager);
}