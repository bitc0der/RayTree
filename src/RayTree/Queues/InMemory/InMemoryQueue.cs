using System;
using System.Collections.Generic;

namespace RayTree.Queues.InMemory;

internal sealed class InMemoryQueue : IQueue
{
	private readonly Queue<object> _queue = [];

	private IQueue.HandleMessage? _handlers;

	public string Name { get; }

	public InMemoryQueue(string name)
	{
		Name = name ?? throw new ArgumentNullException(nameof(name));
	}

	public void Send<TMessage>(TMessage message)
		where TMessage : class
	{
		ArgumentNullException.ThrowIfNull(message);

		_queue.Enqueue(message);
	}

	public void Subscribe(IQueue.HandleMessage handleMessage)
	{
		_handlers += handleMessage ?? throw new ArgumentNullException(nameof(handleMessage));
	}

	public void Unsubscribe(IQueue.HandleMessage handleMessage)
	{
		_handlers -= handleMessage ?? throw new ArgumentNullException( nameof(handleMessage));
	}
}