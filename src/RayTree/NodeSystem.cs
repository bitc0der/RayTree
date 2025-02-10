using System;
using RayTree.Local;
using RayTree.Handlers;

namespace RayTree;

public class NodeSystem
{
	private readonly LocalNode _local;

	private NodeSystem(LocalNode local)
	{
		_local = local ?? throw new ArgumentNullException(nameof(local));
	}

	public static NodeSystem Create(string? id = null)
	{
		var localNodeId = id ?? Guid.NewGuid().ToString();

		var local = new LocalNode(id: localNodeId);

		return new NodeSystem(local);
	}

	public void Register<TMessage>(IMessageHandler<TMessage> handler)
		where TMessage : class
	{
		ArgumentNullException.ThrowIfNull(handler);

		throw new NotImplementedException();
	}

	public void Raise<TMessage>(TMessage message)
	{
		ArgumentNullException.ThrowIfNull(message);

		throw new NotImplementedException();
	}
}