namespace RayTree.Queues.InMemory;

public sealed class InMemoryQueueProvider : IQueueProvider
{
	public string QueueType => "inmemory";

	public IQueue Create(string queueName) => new InMemoryQueue(queueName);
}