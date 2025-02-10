using System;

namespace RayTree.Local;

internal sealed class LocalNodeRouter
{
	public void Route<TMessage>(TMessage message)
		where TMessage : class
	{
		ArgumentNullException.ThrowIfNull(message);

		throw new NotImplementedException();
	}
}