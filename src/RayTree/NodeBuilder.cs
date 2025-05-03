using System;
using System.Collections.Generic;
using RayTree.Handlers;
using RayTree.Local;
using RayTree.Location;

namespace RayTree;

public sealed class NodeBuilder
{
	private readonly NodeSystem _system;
	private readonly string _id;
	private readonly IMessageHandler _messageHandler;

	private readonly Dictionary<Uri, HashSet<Type>> _subMessageTypes = new();

	internal NodeBuilder(NodeSystem system, string id, IMessageHandler messageHandler)
	{
		_system = system ?? throw new ArgumentNullException(nameof(system));

		if (string.IsNullOrWhiteSpace(id))
			throw new ArgumentException($"'{nameof(id)}' cannot be null or whitespace.", nameof(id));

		_id = id;
		_messageHandler = messageHandler ?? throw new ArgumentNullException(nameof(messageHandler));
	}

	public NodeBuilder Handle<TMessage>(ILocation nodeLocation)
	{
		if (!_subMessageTypes.TryGetValue(nodeLocation.Uri, out var subMessageTypes))
		{
			subMessageTypes = [];
		}
		var messageType = typeof(TMessage);
		subMessageTypes.Add(messageType);
		return this;
	}

	internal LocalNode Build()
	{
		var queue = _system.QueueManager.GetOrCreate(queueName: _id);

		var nodeLocation = new NodeLocation(_system.Location, nodeId: _id);

		return new LocalNode(nodeLocation, queue, _messageHandler);
	}
}