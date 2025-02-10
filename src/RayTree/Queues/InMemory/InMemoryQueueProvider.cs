namespace RayTree.Queues.InMemory;

public sealed class InMemoryQueueProvider : IQueueProvider
{
	public IQueue Create(string queueName) => new InMemoryQueue(queueName);
}