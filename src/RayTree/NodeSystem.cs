using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RayTree.Handlers;
using RayTree.Local;
using RayTree.Queues;
using RayTree.Queues.InMemory;
using System.Threading;
using RayTree.Location;

namespace RayTree;

public sealed class NodeSystem : IAsyncDisposable
{
	private readonly Dictionary<string, LocalNode> _nodes = new();
	private readonly SystemMessageHandler _systemMessageHandler;

	internal QueueManager QueueManager { get; }

	public SystemLocation Location { get; }

	private readonly INode _systemNode;

	public NodeSystem(string? id = null, IQueueProvider? queueProvider = null)
	{
		id ??= Guid.NewGuid().ToString();
		queueProvider ??= new InMemoryQueueProvider();

		Location = SystemLocation.Create(systemId: id, queueType: queueProvider.QueueType);

		QueueManager = new QueueManager(queueProvider);
		_systemMessageHandler = new (this);

		_systemNode = CreateNode(
			nodeId: id,
			messageHandler: _systemMessageHandler,
			config: builder =>
			{
				// do nothing
			}
		);
	}

	public void Start()
	{
		QueueManager.Start();

		foreach (var node in _nodes.Values)
		{
			node.Start();
		}
	}

	public async Task StopAsync()
	{
		foreach (var node in _nodes.Values)
		{
			await node.StopAsync();
		}

		await QueueManager.StopAsync();
	}

	public INode CreateNode(IMessageHandler messageHandler, Action<NodeBuilder> config, string? nodeId = null)
	{
		ArgumentNullException.ThrowIfNull(messageHandler);
		ArgumentNullException.ThrowIfNull(config);

		nodeId ??= Guid.NewGuid().ToString();

		var builder = new NodeBuilder(system: this, id: nodeId, messageHandler);

		config(builder);

		return builder.Build();
	}

	public ValueTask ProcessAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
		where TMessage : class
	{
		ArgumentNullException.ThrowIfNull(message);

		return _systemNode.ProcessAsync(message, cancellationToken);
	}

	public async ValueTask DisposeAsync()
	{
		await DisposeAsyncCore().ConfigureAwait(false);
	}

	private async ValueTask DisposeAsyncCore()
	{
		await QueueManager.DisposeAsync();
	}

	private sealed class SystemMessageHandler : IMessageHandler
	{
		private readonly NodeSystem _nodeSystem;

		public SystemMessageHandler(NodeSystem nodeSystem)
		{
			_nodeSystem = nodeSystem ?? throw new ArgumentNullException(nameof(nodeSystem));
		}

		public ValueTask HandleAsync(object message, CancellationToken cancellationToken)
		{
			// FFU

			return ValueTask.CompletedTask;
		}
	}
}