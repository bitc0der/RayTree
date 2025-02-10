using System;

namespace RayTree.Queues.InMemory;

internal sealed class InMemoryQueue : IQueue
{
	public string Name { get; }

	public InMemoryQueue(string name)
	{
		Name = name ?? throw new ArgumentNullException(nameof(name));
	}

	public void Send<TMessage>(TMessage message) where TMessage : class => throw new System.NotImplementedException();
	public void Start() => throw new System.NotImplementedException();
	public void Stop() => throw new System.NotImplementedException();
	public void Subscribe(IQueue.HandleMessage handleMessage) => throw new System.NotImplementedException();
	public void Unsubscribe(IQueue.HandleMessage handleMessage) => throw new System.NotImplementedException();
}