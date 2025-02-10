using System;

namespace RayTree.Remote;

internal sealed class RemoteNode : INode
{
	public string Id { get; }

	public RemoteNode(string id)
	{
		Id = id ?? throw new ArgumentNullException(nameof(id));
	}

	public void Raise<TMessage>(TMessage message)
		where TMessage : class
	{
		throw new System.NotImplementedException();
	}
}