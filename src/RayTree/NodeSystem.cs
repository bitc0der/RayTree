using System;
using System.Threading.Tasks;
using RayTree.Local;
using RayTree.Handlers;
using RayTree.Queues;
using RayTree.Routing;
using RayTree.Queues.InMemory;

namespace RayTree;

public sealed class NodeSystem : IAsyncDisposable
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

	public void Start()
	{
		_queueManager.Start();
	}

	public async Task StopAsync()
	{
		await _queueManager.StopAsync();
	}

	public void Raise<TMessage>(string pipeName, TMessage message)
		where TMessage : class
	{
		ArgumentNullException.ThrowIfNull(pipeName);
		ArgumentNullException.ThrowIfNull(message);

		_local.Raise(pipeName, message);
	}

	public async ValueTask DisposeAsync()
	{
		await DisposeAsyncCore().ConfigureAwait(false);
	}

	private async ValueTask DisposeAsyncCore()
	{
		await _queueManager.DisposeAsync();
	}
}