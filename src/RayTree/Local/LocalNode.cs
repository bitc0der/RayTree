using System;
using RayTree.Routing;

namespace RayTree.Local;

internal sealed class LocalNode : INode
{
	private readonly MessageRouter _messageRouter;

	public string Id { get; }

	public LocalNode(string id, MessageRouter messageRouter)
	{
		Id = id ?? throw new ArgumentNullException(nameof(id));
		_messageRouter = messageRouter ?? throw new ArgumentNullException(nameof(messageRouter));
	}

	public void Raise<TMessage>(string pipeName, TMessage message)
		where TMessage : class
	{
		ArgumentNullException.ThrowIfNull(pipeName);
		ArgumentNullException.ThrowIfNull(message);

		_messageRouter.Route(source: this, pipeName, message);
	}
}