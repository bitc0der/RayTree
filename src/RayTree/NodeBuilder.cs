using System;
using System.Collections.Generic;
using RayTree.Handlers;
using RayTree.Local;
using RayTree.Queues;

namespace RayTree;

public sealed class NodeBuilder
{
	private readonly string _id;
	private readonly IMessageHandler _messageHandler;

	private readonly HashSet<Type> _subMessageTypes = new();

	internal NodeBuilder(string Id, IMessageHandler messageHandler)
	{
		if (string.IsNullOrWhiteSpace(Id))
			throw new ArgumentException($"'{nameof(Id)}' cannot be null or whitespace.", nameof(Id));

		_id = Id;
		_messageHandler = messageHandler ?? throw new ArgumentNullException(nameof(messageHandler));
	}

	public NodeBuilder Handle<TMessage>()
	{
		var messageType = typeof(TMessage);
		_subMessageTypes.Add(messageType);
		return this;
	}

	internal LocalNode Build(QueueManager queueManager)
	{
		ArgumentNullException.ThrowIfNull(queueManager);

		var queue = queueManager.GetOrCreate(queueName: _id);

		return new LocalNode(id: _id, queue, _messageHandler);
	}
}