using System;
using RayTree.Local;
using RayTree.Handlers;
using RayTree.Queues;
using RayTree.Routing;

namespace RayTree;

public sealed class NodeSystem
{
	private readonly LocalNode _local;
	private readonly MessageRouter _router;
	private readonly QueueManager _queueManager;

	private NodeSystem(
		MessageRouter router,
		QueueManager queueManager,
		LocalNode local)
	{
		_router = router ?? throw new ArgumentNullException(nameof(router));
		_local = local ?? throw new ArgumentNullException(nameof(local));
		_queueManager = queueManager ?? throw new ArgumentNullException(nameof(queueManager));
	}

	public static NodeSystem Create(string? id = null, IQueueProvider? queueProvider = null)
	{
		var localNodeId = id ?? Guid.NewGuid().ToString();

		var queueManager = new QueueManager(queueProvider ?? new InMemoryQueueProvider());
		var router = new MessageRouter(queueManager);
		var local = new LocalNode(id: localNodeId, router);

		return new NodeSystem(router, queueManager, local);
	}

	public void Register(IMessageHandler handler)
	{
		ArgumentNullException.ThrowIfNull(handler);

		_router.Register(handler);
	}

	public void Raise<TMessage>(TMessage message)
		where TMessage : class
	{
		ArgumentNullException.ThrowIfNull(message);

		_local.Raise(message);
	}
}