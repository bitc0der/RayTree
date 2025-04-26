using System;
using System.Threading;
using System.Threading.Tasks;

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

	public ValueTask ProcessAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
		where TMessage : class
	{
		throw new NotImplementedException();
	}
}