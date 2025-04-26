using RayTree.Queues;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RayTree.Local;

public sealed class NodeContext
{
	private static readonly AsyncLocal<NodeContext?> _context = new();

	private readonly IQueue _outputQueue;
	
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

	public NodeContext(IQueue outputQueue)
	{
		_outputQueue = outputQueue ?? throw new ArgumentNullException(nameof(outputQueue));
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

	public async ValueTask SendAsync<TMessage>(TMessage message, CancellationToken cancellationToken)
		where TMessage : class
	{
		await _outputQueue.SendAsync(message, cancellationToken);
	}
}