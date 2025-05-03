using System;
using System.Threading;

namespace RayTree.Local;

public sealed class NodeContext
{
	private static readonly AsyncLocal<NodeContext?> _context = new();

	public static NodeContext Current
	{
		get
		{
			var context = _context.Value;
			if (context is null)
				throw new InvalidOperationException("Context is not defined");
			return context;
		}
	}

	internal static void Set(NodeContext context)
	{
		if (context is null)
			throw new ArgumentNullException(nameof(context));

		_context.Value = context;
	}

	internal static void Reset()
	{
		_context.Value = null;
	}
}