namespace RayTree.Queues.InMemory;

internal sealed class InMemoryQueue : IQueue
{
	public void Send<TMessage>(TMessage message) where TMessage : class => throw new System.NotImplementedException();
	public void Start() => throw new System.NotImplementedException();
	public void Stop() => throw new System.NotImplementedException();
	public void Subscribe(IQueue.HandleMessage handleMessage) => throw new System.NotImplementedException();
	public void Unsubscribe(IQueue.HandleMessage handleMessage) => throw new System.NotImplementedException();
}