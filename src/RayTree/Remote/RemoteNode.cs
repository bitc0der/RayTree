using RayTree.Location;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RayTree.Remote;

internal sealed class RemoteNode : INode
{
	public NodeLocation Location { get; }

	public RemoteNode(NodeLocation location)
	{
		Location = location ?? throw new ArgumentNullException(nameof(location));
	}

	public void Raise<TMessage>(TMessage message)
		where TMessage : class
	{
		throw new NotImplementedException();
	}

	public ValueTask ProcessAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
		where TMessage : class
	{
		throw new NotImplementedException();
	}
}