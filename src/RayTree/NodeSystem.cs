using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RayTree.Handlers;
using RayTree.Local;
using RayTree.Queues;
using RayTree.Queues.InMemory;
using System.Threading;

namespace RayTree;

public sealed class NodeSystem : IAsyncDisposable
{
	private readonly QueueManager _queueManager;

	private readonly Dictionary<string, LocalNode> _nodes = new();
	private readonly SystemMessageHandler _systemMessageHandler;
	public string Id { get; }
	public INode SystemNode { get; }

	public NodeSystem(string? id = null, IQueueProvider? queueProvider = null)
	{
		Id = id ?? Guid.NewGuid().ToString();

		_queueManager = new QueueManager(queueProvider ?? new InMemoryQueueProvider());
		_systemMessageHandler = new (this);

		SystemNode = CreateNode(
			nodeId: Id,
			messageHandler: _systemMessageHandler,
			config: builder =>
			{
				// do nothing
			}
		);
	}

	public void Start()
	{
		_queueManager.Start();

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

		await _queueManager.StopAsync();
	}

	public INode CreateNode(IMessageHandler messageHandler, Action<NodeBuilder> config, string? nodeId = null)
	{
		ArgumentNullException.ThrowIfNull(messageHandler);
		ArgumentNullException.ThrowIfNull(config);

		nodeId ??= Guid.NewGuid().ToString();

		var builder = new NodeBuilder(nodeId, messageHandler);

		config(builder);

		return builder.Build(_queueManager);
	}

	public async ValueTask DisposeAsync()
	{
		await DisposeAsyncCore().ConfigureAwait(false);
	}

	private async ValueTask DisposeAsyncCore()
	{
		await _queueManager.DisposeAsync();
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