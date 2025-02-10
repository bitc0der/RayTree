using System;

namespace RayTree.Local;

internal sealed class LocalNode : INode
{
	private readonly LocalNodeRouter _router = new();

	public string Id { get; }

	public LocalNode(string id)
	{
		Id = id ?? throw new ArgumentNullException(nameof(id));
	}

	public void Raise<TMessage>(TMessage message)
		where TMessage : class
	{
		ArgumentNullException.ThrowIfNull(message);

		_router.Route(message);
	}
}