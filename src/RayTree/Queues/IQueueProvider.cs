namespace RayTree.Queues;

public interface IQueueProvider
{
	string QueueType { get; }

	IQueue Create(string queueName);
}